namespace CodexWallpaperSkin;

/// <summary>
/// What one frame actually costs on the wire. Section 3 asks to avoid
/// per-frame full-frame transfer "where practical", and ADR-007 closed the
/// alternative HTTP channel, so any further transport work has to be justified
/// by these numbers rather than by a hunch.
/// </summary>
public sealed record CaptureTransportMetrics(
    long PublishedBytes,
    int LastFrameBytes,
    int MaximumFrameBytes,
    long PublishCalls,
    double TotalPublishMilliseconds)
{
    public static CaptureTransportMetrics Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Mean encoded size of a transported frame.</summary>
    public double AverageFrameBytes => PublishCalls == 0 ? 0 : (double)PublishedBytes / PublishCalls;

    /// <summary>Mean time spent inside one frame transfer, transport included.</summary>
    public double AveragePublishMilliseconds => PublishCalls == 0 ? 0 : TotalPublishMilliseconds / PublishCalls;

    public string Describe() =>
        $"{AverageFrameBytes:0} B/frame average (max {MaximumFrameBytes} B), "
        + $"{AveragePublishMilliseconds:0.00} ms per transfer over {PublishCalls} frame(s)";
}
