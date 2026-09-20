namespace CodexWallpaperSkin;

/// <summary>Facts about the configured loopback endpoint, before queue context.</summary>
public sealed record EndpointProbe(
    bool EndpointIsLoopback,
    bool PortHasListener,
    bool ListenerVerifiedAsCodex,
    bool CodexPageAvailable,
    int OfficialCodexProcessCount);

/// <summary>
/// The operating-system side of connection recovery. Everything that needs
/// Windows, the registry or wall-clock time sits behind this seam so the state
/// machine itself is deterministic in automated tests.
/// </summary>
public interface IConnectionEnvironment
{
    DateTimeOffset UtcNow { get; }

    TimeSpan ReadinessTimeout { get; }

    TimeSpan ReadinessPollInterval { get; }

    Task<EndpointProbe> ProbeAsync(string endpoint, CancellationToken cancellationToken);

    string CreateUnusedEndpoint();

    Task ActivateAsync(string aumid, string endpoint, CancellationToken cancellationToken);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>A verified attachment to the Codex app page.</summary>
public interface ICodexAttachSession
{
    bool IsConnected { get; }

    Task AttachAsync(string endpoint, CancellationToken cancellationToken);

    Task<WallpaperApplyResult> ApplyAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public sealed class CdpAttachSession : ICodexAttachSession
{
    private readonly CdpInjectionService _injection;

    public CdpAttachSession(CdpInjectionService injection)
    {
        _injection = injection ?? throw new ArgumentNullException(nameof(injection));
    }

    public bool IsConnected => _injection.IsConnected;

    public async Task AttachAsync(string endpoint, CancellationToken cancellationToken) =>
        await _injection.ConnectAsync(endpoint, cancellationToken);

    public Task<WallpaperApplyResult> ApplyAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        _injection.ApplyAsync(wallpaper, settings, progress, cancellationToken);
}

public enum ConnectionOutcome
{
    Connected,
    Queued,
    RetryFailed
}

public sealed record ConnectionAttemptResult(
    ConnectionOutcome Outcome,
    CodexConnectionState State,
    string Guidance,
    WallpaperEntry? AppliedWallpaper = null,
    WallpaperApplyResult? ApplyResult = null,
    bool ActivatedCodex = false,
    QueueFailureReason Failure = QueueFailureReason.None)
{
    public bool IsConnected => Outcome == ConnectionOutcome.Connected;
}

/// <summary>
/// Implements section 2a: one entry point that turns an unreachable or
/// not-yet-ready Codex into either a verified connection, an explicitly
/// explained queued wallpaper, or a bounded retry failure. It never terminates
/// Codex, never attaches to an unverified listener, and never reports a queued
/// wallpaper as applied.
/// </summary>
public static class ConnectionCoordinator
{
    public static async Task<ConnectionAttemptResult> ConnectAsync(
        AppState state,
        ICodexAttachSession session,
        IConnectionEnvironment environment,
        bool applyQueuedWallpaper,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(environment);

        if (!CdpEndpoint.IsLoopbackHttp(state.CdpBaseUrl))
        {
            // Section 2a: recovery guidance must stay actionable, so an unusable
            // saved endpoint is replaced instead of reported as a raw error.
            state.CdpBaseUrl = environment.CreateUnusedEndpoint();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var probe = await ProbeAsync(state, environment, cancellationToken);

        if (probe.PortHasListener && !probe.ListenerVerifiedAsCodex && !probe.CodexPageAvailable)
        {
            // An unverified program holds the port. Move to a fresh loopback port
            // rather than attaching to it, replacing it, or killing it.
            state.CdpBaseUrl = environment.CreateUnusedEndpoint();
            cancellationToken.ThrowIfCancellationRequested();
            probe = await ProbeAsync(state, environment, cancellationToken);
        }

        // This is a fact check, not the GUI state: a queue may already exist, and
        // Codex being open without its channel still forbids activation.
        var codexIsRunningWithoutChannel = !probe.PortHasListener
            && !probe.CodexPageAvailable
            && probe.OfficialCodexProcessCount > 0;
        if (codexIsRunningWithoutChannel)
        {
            // Codex is already open without CDP. Chromium cannot gain a
            // startup-only debugging channel retroactively, and the user's
            // current task must not be interrupted.
            return Queue(state, environment, QueueFailureReason.CodexRunningWithoutCdp, ConnectionOutcome.Queued);
        }

        var activated = false;
        if (!probe.CodexPageAvailable)
        {
            var codexIsStarting = probe.PortHasListener && probe.ListenerVerifiedAsCodex;
            if (!codexIsStarting)
            {
                try
                {
                    await environment.ActivateAsync(ResolveAumid(state), state.CdpBaseUrl, cancellationToken);
                    activated = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return Queue(state, environment, QueueFailureReason.ActivationFailed, ConnectionOutcome.RetryFailed);
                }
            }

            if (!await WaitForReadinessAsync(session, state, environment, cancellationToken))
            {
                return Queue(state, environment, QueueFailureReason.CdpNotReady, ConnectionOutcome.RetryFailed);
            }
        }

        if (!session.IsConnected)
        {
            try
            {
                await session.AttachAsync(state.CdpBaseUrl, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Queue(state, environment, QueueFailureReason.CdpNotReady, ConnectionOutcome.RetryFailed);
            }
        }

        if (!applyQueuedWallpaper)
        {
            // Attached without an apply request: an existing queue stays exactly
            // as it was, and nothing is reported as applied.
            return new ConnectionAttemptResult(
                ConnectionOutcome.Connected,
                CodexConnectionState.CodexConnected,
                ConnectionRecovery.Guidance(CodexConnectionState.CodexConnected),
                ActivatedCodex: activated);
        }

        var queued = ResolveRequestedWallpaper(state, out var fromQueue);
        if (queued is not null && !fromQueue && !state.AutoRestoreOnLaunch)
        {
            // A previously applied wallpaper is only re-applied on connect when
            // the user kept automatic restore enabled. An explicitly queued
            // request is always honored.
            queued = null;
        }
        if (queued is null)
        {
            return new ConnectionAttemptResult(
                ConnectionOutcome.Connected,
                CodexConnectionState.CodexConnected,
                ConnectionRecovery.Guidance(CodexConnectionState.CodexConnected),
                ActivatedCodex: activated);
        }
        if (!queued.CanApply)
        {
            return new ConnectionAttemptResult(
                ConnectionOutcome.RetryFailed,
                CodexConnectionState.RetryFailed,
                "The requested wallpaper is no longer available: " + queued.Note,
                Failure: QueueFailureReason.CdpNotReady);
        }

        try
        {
            var applyResult = await session.ApplyAsync(queued, state.Settings, progress, cancellationToken);
            state.LastAppliedWallpaperId = queued.Id;
            WallpaperQueue.Clear(state);
            var suffix = fromQueue ? string.Empty : " (restored from the previous session)";
            return new ConnectionAttemptResult(
                ConnectionOutcome.Connected,
                CodexConnectionState.CodexConnected,
                $"Connected and applied {queued.Title}{suffix}."
                + (activated ? " Codex was started with its verified local channel." : string.Empty),
                queued,
                applyResult,
                activated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Queue(state, environment, QueueFailureReason.ActivationFailed, ConnectionOutcome.RetryFailed,
                conciseError: exception.Message);
        }
    }

    /// <summary>
    /// Passively attaches when Codex is already reachable, without activating
    /// anything. Used when the controller opens and a queue is waiting.
    /// </summary>
    public static async Task<bool> TryAttachWithoutActivationAsync(
        AppState state,
        ICodexAttachSession session,
        IConnectionEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!CdpEndpoint.IsLoopbackHttp(state.CdpBaseUrl) || session.IsConnected)
        {
            return session.IsConnected;
        }
        try
        {
            var probe = await ProbeAsync(state, environment, cancellationToken);
            if (!probe.CodexPageAvailable)
            {
                return false;
            }
            await session.AttachAsync(state.CdpBaseUrl, cancellationToken);
            return session.IsConnected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ConnectionProbe> ProbeAsync(
        AppState state,
        IConnectionEnvironment environment,
        CancellationToken cancellationToken)
    {
        var endpoint = await environment.ProbeAsync(state.CdpBaseUrl, cancellationToken);
        return new ConnectionProbe(
            endpoint.EndpointIsLoopback,
            endpoint.PortHasListener,
            endpoint.ListenerVerifiedAsCodex,
            endpoint.CodexPageAvailable,
            endpoint.OfficialCodexProcessCount,
            WallpaperQueue.HasQueued(state),
            state.PendingLastFailure);
    }

    private static async Task<bool> WaitForReadinessAsync(
        ICodexAttachSession session,
        AppState state,
        IConnectionEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (session.IsConnected)
        {
            return true;
        }
        var deadline = environment.UtcNow + environment.ReadinessTimeout;
        while (environment.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await session.AttachAsync(state.CdpBaseUrl, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await environment.DelayAsync(environment.ReadinessPollInterval, cancellationToken);
            }
        }
        return session.IsConnected;
    }

    private static ConnectionAttemptResult Queue(
        AppState state,
        IConnectionEnvironment environment,
        QueueFailureReason reason,
        ConnectionOutcome outcome,
        string? conciseError = null)
    {
        var wallpaperId = WallpaperQueue.QueuedWallpaperId(state) ?? state.LastAppliedWallpaperId;
        if (!string.IsNullOrWhiteSpace(wallpaperId))
        {
            if (WallpaperQueue.HasQueued(state))
            {
                WallpaperQueue.RecordFailure(state, reason, environment.UtcNow);
            }
            else
            {
                WallpaperQueue.Enqueue(state, wallpaperId!, reason, environment.UtcNow);
            }
        }

        var connectionState = outcome == ConnectionOutcome.Queued
            ? CodexConnectionState.Queued
            : CodexConnectionState.RetryFailed;
        var guidance = outcome == ConnectionOutcome.Queued
            ? ConnectionRecovery.Guidance(CodexConnectionState.Queued)
            : ConnectionRecovery.Guidance(CodexConnectionState.RetryFailed);
        if (!string.IsNullOrWhiteSpace(conciseError) && !WallpaperQueue.HasQueued(state))
        {
            guidance = "The wallpaper could not be applied: " + conciseError;
        }
        return new ConnectionAttemptResult(outcome, connectionState, guidance, Failure: reason);
    }

    /// <summary>
    /// The wallpaper an apply attempt should target: an explicit queue first,
    /// otherwise the last successfully applied wallpaper.
    /// </summary>
    private static WallpaperEntry? ResolveRequestedWallpaper(AppState state, out bool fromQueue)
    {
        if (WallpaperQueue.HasQueued(state))
        {
            fromQueue = true;
            return AutoRestoreService.ResolveLastWallpaper(state);
        }
        fromQueue = false;
        return state.LastAppliedWallpaperId is null ? null : AutoRestoreService.ResolveLastWallpaper(state);
    }

    private static string ResolveAumid(AppState state) =>
        AppActivation.IsOfficialAumid(state.Aumid) ? state.Aumid! : AppActivation.OfficialAumid;
}
