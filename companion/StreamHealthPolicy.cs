namespace CodexWallpaperSkin;

public enum LiveStreamHealthKind
{
    Healthy,
    WarmingUp,
    PausedWhileHidden,
    StreamEnded,
    CaptureStalled,
    EncoderStalled,
    TransportStalled,
    PresenterStalled,
    PerformanceDegraded
}

public sealed record LiveStreamHealthDecision(
    LiveStreamHealthKind Kind,
    bool ShouldRecover,
    string Reason);

public static class StreamHealthPolicy
{
    public const int MaximumRecoveryAttempts = 2;
    internal static readonly TimeSpan WarmupWindow = TimeSpan.FromSeconds(18);
    internal static readonly TimeSpan PresentationStaleAfter = TimeSpan.FromSeconds(20);

    public static LiveStreamHealthDecision Evaluate(
        ActiveStreamDiagnostics diagnostics,
        bool pauseWhenHidden,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (pauseWhenHidden && diagnostics.PageHidden)
        {
            return Decision(LiveStreamHealthKind.PausedWhileHidden, false,
                "The Codex page is hidden and hidden-window pausing is enabled.");
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.TransportError))
        {
            return Decision(LiveStreamHealthKind.TransportStalled, true,
                "The bounded local stream publisher reported an error.");
        }

        var streamAge = TimeSpan.FromSeconds(Math.Max(0, diagnostics.Native?.ElapsedSeconds ?? 0));
        if (diagnostics.LastPresentation is null && streamAge < WarmupWindow)
        {
            return Decision(LiveStreamHealthKind.WarmingUp, false,
                "The stream is still inside its first-frame grace period.");
        }

        if (diagnostics.LastPresentation is DateTimeOffset lastPresentation
            && now - lastPresentation <= PresentationStaleAfter)
        {
            if (diagnostics.Mode.Equals("h264-webcodecs", StringComparison.OrdinalIgnoreCase)
                && streamAge >= TimeSpan.FromSeconds(5)
                && diagnostics.Received >= 60
                && (diagnostics.Presented * 2 < diagnostics.Received
                    || diagnostics.Dropped * 3 > diagnostics.Received))
            {
                return Decision(LiveStreamHealthKind.PerformanceDegraded, false,
                    "Codex is receiving frames, but the presentation queue is discarding too many of them.");
            }
            return Decision(LiveStreamHealthKind.Healthy, false,
                "Codex presented a recent frame.");
        }

        var native = diagnostics.Native;
        if (diagnostics.Mode.Equals("h264-webcodecs", StringComparison.OrdinalIgnoreCase))
        {
            if (native is null || native.CapturedFrames == 0)
            {
                return Decision(LiveStreamHealthKind.CaptureStalled, true,
                    "Windows capture stopped producing frames.");
            }
            if (native.EncoderInputs == 0 || native.EncodedFrames == 0)
            {
                return Decision(LiveStreamHealthKind.EncoderStalled, true,
                    "The hardware encoder stopped producing output.");
            }
        }

        if (diagnostics.Received == 0)
        {
            return Decision(LiveStreamHealthKind.TransportStalled, true,
                "Codex stopped receiving the bounded local stream.");
        }

        return Decision(LiveStreamHealthKind.PresenterStalled, true,
            diagnostics.DecodeErrors > 0
                ? "Codex stopped presenting frames after decoder errors."
                : "Codex stopped presenting received frames.");
    }

    public static bool HasRecoveryBudget(int attempts) =>
        attempts >= 0 && attempts < MaximumRecoveryAttempts;

    private static LiveStreamHealthDecision Decision(
        LiveStreamHealthKind kind,
        bool shouldRecover,
        string reason) => new(kind, shouldRecover, reason);
}
