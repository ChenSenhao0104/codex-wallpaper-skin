namespace CodexWallpaperSkin;

/// <summary>
/// The six product states required for Windows restart and connection recovery.
/// They are deliberately user-facing: each one maps to a short badge, one
/// actionable sentence, and an optional queue panel.
/// </summary>
public enum CodexConnectionState
{
    CodexClosed,
    CodexStarting,
    CodexConnected,
    RunningWithoutCdp,
    Queued,
    RetryFailed
}

/// <summary>
/// Why a wallpaper is still waiting. Only a bounded reason code is persisted so
/// that recovery guidance never has to store process diagnostics or file paths.
/// </summary>
public enum QueueFailureReason
{
    None,
    CodexRunningWithoutCdp,
    CdpNotReady,
    ActivationFailed,
    EndpointConflict
}

/// <summary>
/// Immutable, already-collected facts about the configured endpoint. Keeping the
/// probe separate from the classification makes the whole state machine
/// reproducible in automated tests without a Codex installation.
/// </summary>
public sealed record ConnectionProbe(
    bool EndpointIsLoopback,
    bool PortHasListener,
    bool ListenerVerifiedAsCodex,
    bool CodexPageAvailable,
    int OfficialCodexProcessCount,
    bool HasQueuedWallpaper,
    QueueFailureReason QueuedFailure);

public static class ConnectionRecovery
{
    /// <summary>
    /// Maps observed facts to exactly one user-facing state. Order matters: a
    /// reachable Codex page always wins, and a queued wallpaper is reported as
    /// queued rather than as an applied wallpaper.
    /// </summary>
    public static CodexConnectionState Classify(ConnectionProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!probe.EndpointIsLoopback)
        {
            return CodexConnectionState.RetryFailed;
        }
        if (probe.CodexPageAvailable)
        {
            return CodexConnectionState.CodexConnected;
        }
        if (probe.PortHasListener && probe.ListenerVerifiedAsCodex)
        {
            // The verified Codex process already owns the loopback port, so it is
            // starting with CDP; asking Windows to activate it again would only
            // foreground it and could not add a second debugging channel.
            return CodexConnectionState.CodexStarting;
        }
        if (probe.HasQueuedWallpaper && probe.QueuedFailure != QueueFailureReason.None)
        {
            return CodexConnectionState.RetryFailed;
        }
        if (probe.HasQueuedWallpaper)
        {
            return CodexConnectionState.Queued;
        }
        if (probe.PortHasListener)
        {
            // An unverified listener holds the port. Connect recovers by moving to
            // a fresh loopback port instead of attaching to it or killing it.
            return CodexConnectionState.RetryFailed;
        }
        if (probe.OfficialCodexProcessCount > 0)
        {
            return CodexConnectionState.RunningWithoutCdp;
        }
        return CodexConnectionState.CodexClosed;
    }

    public static string Badge(CodexConnectionState state) => state switch
    {
        CodexConnectionState.CodexClosed => "Codex closed",
        CodexConnectionState.CodexStarting => "Codex starting",
        CodexConnectionState.CodexConnected => "Connected",
        CodexConnectionState.RunningWithoutCdp => "Running without CDP",
        CodexConnectionState.Queued => "Wallpaper queued",
        _ => "Retry needed"
    };

    /// <summary>
    /// One concise, actionable sentence. Technical diagnostics stay in Doctor;
    /// this text never contains raw listener or process errors.
    /// </summary>
    public static string Guidance(CodexConnectionState state) => state switch
    {
        CodexConnectionState.CodexClosed =>
            "Codex is not running. Connect starts it with its verified local channel, then applies your wallpaper.",
        CodexConnectionState.CodexStarting =>
            "Codex is starting with its local wallpaper channel. Connect finishes automatically when it is ready.",
        CodexConnectionState.CodexConnected =>
            "Connected to Codex. Apply a wallpaper, adjust it live, or restore the original background.",
        CodexConnectionState.RunningWithoutCdp =>
            "Codex is already open without its local wallpaper channel. Chromium can only enable that channel while Codex starts, "
            + "so the current task was left untouched and your wallpaper is queued.",
        CodexConnectionState.Queued =>
            "A wallpaper is queued and has not been applied yet. Connect when Codex can start with its local channel, or cancel the queue.",
        _ =>
            "The last automatic attempt did not finish. Retry once Codex is closed, or open Doctor for technical details."
    };

    public static string ActionLabel(CodexConnectionState state) => state switch
    {
        CodexConnectionState.CodexClosed or CodexConnectionState.CodexStarting => "Connect & start Codex",
        CodexConnectionState.CodexConnected => "Reconnect",
        _ => "Retry connect"
    };

    /// <summary>Short, non-technical explanation appended to queued/retry text.</summary>
    public static string FailureDetail(QueueFailureReason reason) => reason switch
    {
        QueueFailureReason.CodexRunningWithoutCdp => "Codex was already open without its local channel.",
        QueueFailureReason.CdpNotReady => "Codex did not expose its local channel in time.",
        QueueFailureReason.ActivationFailed => "Windows could not start Codex with its local channel.",
        QueueFailureReason.EndpointConflict => "Another program already uses the saved local port.",
        _ => string.Empty
    };
}
