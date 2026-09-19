namespace CodexWallpaperSkin;

public sealed record AutoRestoreResult(
    WallpaperEntry Wallpaper,
    CdpTarget Target,
    WallpaperApplyResult ApplyResult,
    bool ActivatedCodex);

public sealed class CodexAlreadyRunningWithoutCdpException : InvalidOperationException
{
    public CodexAlreadyRunningWithoutCdpException(IReadOnlyList<int> processIds, Exception? innerException = null)
        : base(
            "Codex is already running without the local wallpaper control channel. "
            + "The wallpaper has been queued and will be restored after Codex is next closed normally; your current task was not interrupted.",
            innerException)
    {
        ProcessIds = processIds;
    }

    public IReadOnlyList<int> ProcessIds { get; }
}

public static class AutoRestoreService
{
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

        CdpTarget? target = null;
        Exception? initialConnectionError = null;
        try
        {
            target = await injection.ConnectAsync(state.CdpBaseUrl, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            initialConnectionError = exception;
        }

        var activated = false;
        if (target is null)
        {
            if (!activateIfNeeded)
            {
                throw new InvalidOperationException("Codex CDP is not available for automatic restore.", initialConnectionError);
            }

            var runningCodex = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
            if (runningCodex.Count > 0)
            {
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
                    // Never attach to or replace an unverified listener. Move to
                    // a fresh loopback port before asking Windows to start Codex.
                    state.CdpBaseUrl = CdpEndpoint.CreateUnusedLoopbackUrl();
                }
            }

            var aumid = AppActivation.IsOfficialAumid(state.Aumid)
                ? state.Aumid!
                : AppActivation.OfficialAumid;
            state.Aumid = aumid;
            await AppActivation.ActivateWithCdpAsync(aumid, state.CdpBaseUrl, cancellationToken);
            activated = true;

            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            Exception? lastError = initialConnectionError;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    target = await injection.ConnectAsync(state.CdpBaseUrl, cancellationToken);
                    break;
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
            if (target is null)
            {
                throw new TimeoutException(
                    "Codex did not expose a verified CDP page within 30 seconds. If Codex was already open without CDP, close it completely once and try again.",
                    lastError);
            }
        }

        var result = await injection.ApplyAsync(wallpaper, state.Settings, progress, cancellationToken);
        state.LastAppliedWallpaperId = wallpaper.Id;
        state.PendingWallpaperId = null;
        state.PendingActivation = false;
        return new AutoRestoreResult(wallpaper, target, result, activated);
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
        if (saved.Source.Equals("Wallpaper Engine", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(saved.ProjectPath))
        {
            try
            {
                return WallpaperCatalog.ParseProject(saved.ProjectPath);
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
