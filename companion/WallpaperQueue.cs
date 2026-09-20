namespace CodexWallpaperSkin;

/// <summary>
/// The durable "requested but not applied" wallpaper. The queue lives in the
/// saved state so that it survives controller closure and a Windows restart, and
/// it never writes <see cref="AppState.LastAppliedWallpaperId"/>, which is the
/// only field the product treats as proof that a wallpaper is on screen.
/// </summary>
public static class WallpaperQueue
{
    public static bool HasQueued(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.PendingActivation && !string.IsNullOrWhiteSpace(state.PendingWallpaperId);
    }

    public static string? QueuedWallpaperId(AppState state) =>
        HasQueued(state) ? state.PendingWallpaperId : null;

    /// <summary>Queues a wallpaper without claiming it was applied.</summary>
    public static void Enqueue(AppState state, string wallpaperId, QueueFailureReason reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(wallpaperId) || wallpaperId.Length > 2048)
        {
            throw new ArgumentException("A bounded wallpaper identifier is required.", nameof(wallpaperId));
        }
        state.PendingWallpaperId = wallpaperId;
        state.PendingActivation = true;
        state.PendingQueuedAt = now;
        state.PendingAttempts = 0;
        state.PendingLastFailure = reason;
    }

    /// <summary>Records one failed automatic attempt while keeping the queue.</summary>
    public static void RecordFailure(AppState state, QueueFailureReason reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!HasQueued(state))
        {
            return;
        }
        state.PendingAttempts = Math.Min(state.PendingAttempts + 1, 1_000);
        state.PendingLastFailure = reason;
        state.PendingLastAttemptAt = now;
    }

    /// <summary>
    /// Clears the queue after a successful apply or an explicit cancellation.
    /// <see cref="AppState.LastAppliedWallpaperId"/> is left untouched so that
    /// cancelling a queue can never look like applying or restoring a wallpaper.
    /// </summary>
    public static void Clear(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.PendingWallpaperId = null;
        state.PendingActivation = false;
        state.PendingQueuedAt = null;
        state.PendingLastAttemptAt = null;
        state.PendingAttempts = 0;
        state.PendingLastFailure = QueueFailureReason.None;
    }

    /// <summary>Concise queue text for the controller window.</summary>
    public static string Describe(AppState state, WallpaperEntry? wallpaper)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!HasQueued(state))
        {
            return "No wallpaper is queued.";
        }
        var title = string.IsNullOrWhiteSpace(wallpaper?.Title) ? "The selected wallpaper" : wallpaper!.Title;
        var detail = ConnectionRecovery.FailureDetail(state.PendingLastFailure);
        var attempts = state.PendingAttempts <= 0
            ? "No automatic attempt has run yet."
            : $"{state.PendingAttempts} automatic attempt(s) did not finish.";
        return string.IsNullOrWhiteSpace(detail)
            ? $"{title} is queued and not applied. {attempts}"
            : $"{title} is queued and not applied. {detail} {attempts}";
    }
}
