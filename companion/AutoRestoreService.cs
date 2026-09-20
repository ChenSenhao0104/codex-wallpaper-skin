namespace CodexWallpaperSkin;

public sealed record AutoRestoreResult(
    WallpaperEntry Wallpaper,
    CdpTarget Target,
    WallpaperApplyResult ApplyResult,
    bool ActivatedCodex);

public sealed record CdpConnectionResult(CdpTarget Target, bool ActivatedCodex);

public sealed class CodexAlreadyRunningWithoutCdpException : InvalidOperationException
{
    public CodexAlreadyRunningWithoutCdpException(IReadOnlyList<int> processIds, Exception? innerException = null)
        : base(
            "Codex is already running without the local wallpaper control channel. Chromium can enable this local channel only when Codex starts. "
            + "The current task was not interrupted.",
            innerException)
    {
        ProcessIds = processIds;
    }

    public IReadOnlyList<int> ProcessIds { get; }
}

public static class AutoRestoreService
{
    public static async Task<CdpConnectionResult> ConnectOrActivateAsync(
        AppState state,
        CdpInjectionService injection,
        bool activateIfNeeded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(injection);

        Exception? initialConnectionError = null;
        try
        {
            var connectedTarget = await injection.ConnectAsync(state.CdpBaseUrl, cancellationToken);
            return new CdpConnectionResult(connectedTarget, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            initialConnectionError = exception;
        }

        if (!activateIfNeeded)
        {
            throw new InvalidOperationException("The Codex wallpaper channel is not available.", initialConnectionError);
        }

        var runningCodex = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
        if (runningCodex.Count > 0)
        {
            // A verified listener can appear slightly before its app:// page.
            // Give that legitimate startup race a short grace period before
            // classifying the process as an already-running no-CDP instance.
            if (!CdpEndpoint.IsAvailableForActivation(state.CdpBaseUrl))
            {
                try
                {
                    var endpoint = CdpEndpoint.Normalize(state.CdpBaseUrl);
                    CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpoint.Port);
                    var lateTarget = await WaitForConnectionAsync(
                        state.CdpBaseUrl, injection, TimeSpan.FromSeconds(10), initialConnectionError, cancellationToken);
                    return new CdpConnectionResult(lateTarget, false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fall through to the safe no-restart state. Never replace
                    // a running user's Codex process merely to add CDP.
                }
            }
            throw new CodexAlreadyRunningWithoutCdpException(runningCodex, initialConnectionError);
        }

        if (!CdpEndpoint.IsAvailableForActivation(state.CdpBaseUrl))
        {
            try
            {
                var endpoint = CdpEndpoint.Normalize(state.CdpBaseUrl);
                CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpoint.Port);
            }
            catch
            {
                // Never attach to or replace an unverified listener. Move to a
                // fresh loopback port before asking Windows to start Codex.
                state.CdpBaseUrl = CdpEndpoint.CreateUnusedLoopbackUrl();
            }
        }

        var aumid = AppActivation.IsOfficialAumid(state.Aumid)
            ? state.Aumid!
            : AppActivation.OfficialAumid;
        state.Aumid = aumid;
        await AppActivation.ActivateWithCdpAsync(aumid, state.CdpBaseUrl, cancellationToken);
        var target = await WaitForConnectionAsync(
            state.CdpBaseUrl, injection, TimeSpan.FromSeconds(30), initialConnectionError, cancellationToken);
        return new CdpConnectionResult(target, true);
    }

    public static async Task<AutoRestoreResult> RestoreAsync(
        AppState state,
        CdpInjectionService injection,
        bool activateIfNeeded,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(injection);

        var wallpaper = ResolveLastWallpaper(state)
            ?? throw new InvalidOperationException("No previously applied wallpaper is saved.");
        if (!wallpaper.CanApply)
        {
            throw new InvalidOperationException($"The last wallpaper is no longer applicable: {wallpaper.Note}");
        }

        var connection = await ConnectOrActivateAsync(
            state, injection, activateIfNeeded, cancellationToken);

        var result = await injection.ApplyAsync(wallpaper, state.Settings, progress, cancellationToken);
        state.LastAppliedWallpaperId = wallpaper.Id;
        state.PendingWallpaperId = null;
        state.PendingActivation = false;
        return new AutoRestoreResult(wallpaper, connection.Target, result, connection.ActivatedCodex);
    }

    private static async Task<CdpTarget> WaitForConnectionAsync(
        string endpoint,
        CdpInjectionService injection,
        TimeSpan duration,
        Exception? initialError,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(duration);
        Exception? lastError = initialError;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await injection.ConnectAsync(endpoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(500, cancellationToken);
            }
        }
        throw new TimeoutException(
            "Codex started, but its verified local wallpaper channel did not become ready within the expected time. Use Doctor for technical details, then try Connect again.",
            lastError);
    }

    internal static WallpaperEntry? ResolveLastWallpaper(AppState state)
    {
        var wallpaperId = state.PendingActivation && !string.IsNullOrWhiteSpace(state.PendingWallpaperId)
            ? state.PendingWallpaperId
            : state.LastAppliedWallpaperId;
        if (string.IsNullOrWhiteSpace(wallpaperId))
        {
            return null;
        }
        var saved = state.Wallpapers.FirstOrDefault(item =>
            item.Id.Equals(wallpaperId, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
        {
            return null;
        }
        var personalizations = WallpaperLibraryStore.Load();
        WallpaperLibraryStore.Apply(saved, personalizations);
        if (saved.Source.Equals("Wallpaper Engine", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(saved.ProjectPath))
        {
            try
            {
                var refreshed = WallpaperCatalog.ParseProject(saved.ProjectPath);
                WallpaperLibraryStore.Apply(refreshed, personalizations);
                return refreshed;
            }
            catch
            {
                // Return the saved entry so the caller receives the normal,
                // specific missing/validation error from the apply path.
            }
        }
        return saved;
    }
}
