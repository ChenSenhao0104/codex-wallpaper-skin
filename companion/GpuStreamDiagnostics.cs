using System.Diagnostics;

namespace CodexWallpaperSkin;

/// <summary>
/// Bounded, privacy-safe v0.4 stream diagnostics.
///
/// The record intentionally contains no captured pixels, no Workshop or project
/// paths, no user state and no credentials: only counters, cadence, queue depth,
/// latency percentiles, encoder/decoder mode, stream identity and recovery
/// history. Latency samples live in a fixed-size reservoir so a long-running
/// wallpaper cannot grow the diagnostic footprint without bound.
/// </summary>
public sealed class GpuStreamDiagnostics
{
    private const int LatencyReservoirSize = 256;

    private readonly long[] _latencySamples = new long[LatencyReservoirSize];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _latencyCount;
    private int _latencyCursor;
    private long _capturedFrames;
    private long _rejectedFrames;
    private long _submittedFrames;
    private long _codedFrames;
    private long _encodedFragments;
    private long _encodedBytes;
    private long _droppedFrames;
    private long _deliveredFragments;
    private long _presentedFragments;
    private long _staleFragments;
    private long _rejectedFragments;
    private long _maximumQueueDepth;
    private long _recoveryCount;
    private long _longestRecoveryTicks;
    private long _lastPresentedUnixMs;

    public GpuStreamDiagnostics(string streamId, string transport, int captureWidth, int captureHeight, int requestedFrameRate)
    {
        StreamId = streamId;
        Transport = transport;
        CaptureWidth = captureWidth;
        CaptureHeight = captureHeight;
        RequestedFrameRate = GpuStreamStatusLabel.NormalizeFrameRate(requestedFrameRate);
    }

    public string StreamId { get; }
    public string Transport { get; }
    public int CaptureWidth { get; }
    public int CaptureHeight { get; }
    public int RequestedFrameRate { get; }
    public string EncoderMode { get; set; } = "unknown";
    public string DecoderMode { get; set; } = "unknown";
    public string Status { get; set; } = GpuStreamStatusLabel.UnsupportedOrFailed;
    public string? Note { get; set; }

    public void CountCaptured() => Interlocked.Increment(ref _capturedFrames);
    public void CountRejected() => Interlocked.Increment(ref _rejectedFrames);
    public void CountDropped() => Interlocked.Increment(ref _droppedFrames);
    public void CountStaleFragment() => Interlocked.Increment(ref _staleFragments);
    public void CountRejectedFragment() => Interlocked.Increment(ref _rejectedFragments);

    /// <summary>
    /// Gauges, not counters. The capture channel holds the most recent frames and
    /// supersedes older ones, so the number left behind is the difference between
    /// what the source published and what this pipeline consumed; accumulating
    /// that difference on every read would report nonsense.
    /// </summary>
    public void SetCaptureGauges(long publishedFrames, long supersededFrames)
    {
        Interlocked.Exchange(ref _publishedFrames, Math.Max(0, publishedFrames));
        Interlocked.Exchange(ref _supersededFrames, Math.Max(0, supersededFrames));
    }

    private long _publishedFrames;
    private long _supersededFrames;

    /// <summary>Counts delivered fragments. One transport message carries a bounded group of them.</summary>
    public void CountDeliveredFragment(int count = 1) => Interlocked.Add(ref _deliveredFragments, Math.Max(0, count));

    /// <summary>Counts one encoded fragment, which is one Media Source Extensions unit.</summary>
    public void CountEncodedFragment(int bytes)
    {
        Interlocked.Increment(ref _encodedFragments);
        Interlocked.Add(ref _encodedBytes, bytes);
    }

    /// <summary>
    /// Counts the coded frames a fragment contains. This is what the product may
    /// claim as its cadence; the number of submitted frames is reported next to it
    /// so padding is visible rather than flattering.
    /// </summary>
    public void CountCodedFrames(int frames) => Interlocked.Add(ref _codedFrames, Math.Max(0, frames));

    /// <summary>Mirrors the encoder's submitted-frame count so the snapshot reports frames, not fragments.</summary>
    public void SetSubmittedFrames(long frames) => Interlocked.Exchange(ref _submittedFrames, Math.Max(0, frames));

    /// <summary>
    /// Average per-frame wait for a captured frame and per-frame encode hand-off
    /// cost. They are separated so a throughput ceiling can be attributed: the
    /// readback itself happens on the capture callback thread, so a large wait
    /// means the source produced frames slowly, not that this pipeline was slow.
    /// </summary>
    public void SetFrameTimings(double waitMs, double submitMs, double slowestWaitMs)
    {
        _averageReadbackMs = Math.Round(waitMs, 2);
        _averageSubmitMs = Math.Round(submitMs, 2);
        _slowestReadbackMs = Math.Round(slowestWaitMs, 2);
    }

    private double _averageReadbackMs;
    private double _averageSubmitMs;
    private double _slowestReadbackMs;

    /// <summary>Counts presented fragments, which is the cadence the product actually claims.</summary>
    public void CountPresented(int count = 1)
    {
        Interlocked.Add(ref _presentedFragments, Math.Max(0, count));
        Interlocked.Exchange(ref _lastPresentedUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Records the encode-to-present latency of one acknowledged fragment.</summary>
    public void RecordLatency(TimeSpan latency)
    {
        var milliseconds = Math.Clamp((long)latency.TotalMilliseconds, 0, 60_000);
        var slot = Interlocked.Increment(ref _latencyCursor) - 1;
        lock (_latencySamples)
        {
            _latencySamples[slot % LatencyReservoirSize] = milliseconds;
            if (_latencyCount < LatencyReservoirSize)
            {
                _latencyCount++;
            }
        }
    }

    public void ObserveQueueDepth(int depth)
    {
        var current = Volatile.Read(ref _maximumQueueDepth);
        while (depth > current)
        {
            var observed = Interlocked.CompareExchange(ref _maximumQueueDepth, depth, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }

    public void CountRecovery(TimeSpan duration)
    {
        Interlocked.Increment(ref _recoveryCount);
        var ticks = duration.Ticks;
        var current = Volatile.Read(ref _longestRecoveryTicks);
        while (ticks > current)
        {
            var observed = Interlocked.CompareExchange(ref _longestRecoveryTicks, ticks, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }

    public GpuStreamDiagnosticsSnapshot Snapshot(GpuStreamStatus status, string? note = null)
    {
        Status = GpuStreamStatusLabel.Describe(status);
        Note = note;
        var seconds = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        var captured = Interlocked.Read(ref _capturedFrames);
        var submitted = Interlocked.Read(ref _submittedFrames);
        var coded = Interlocked.Read(ref _codedFrames);
        var encodedFragments = Interlocked.Read(ref _encodedFragments);
        // The honest cadence is the number of frames actually coded, not the
        // declared rate and not the fragment count.
        var cadenceFrames = coded > 0 ? coded : submitted;
        var presented = Interlocked.Read(ref _presentedFragments);
        var lastPresented = Interlocked.Read(ref _lastPresentedUnixMs);
        var (median, p95) = Percentiles();
        var presentedFps = presented / seconds;
        return new GpuStreamDiagnosticsSnapshot(
            StreamId: StreamId,
            Status: Status,
            Transport: Transport,
            EncoderMode: EncoderMode,
            DecoderMode: DecoderMode,
            CaptureWidth: CaptureWidth,
            CaptureHeight: CaptureHeight,
            RequestedFrameRate: RequestedFrameRate,
            ObservedCapturedFps: Math.Round(captured / seconds, 1),
            ObservedEncodedFps: Math.Round(cadenceFrames / seconds, 1),
            ObservedPresentedFps: Math.Round(presentedFps, 1),
            CapturedFrames: captured,
            PublishedFrames: Interlocked.Read(ref _publishedFrames),
            SupersededFrames: Interlocked.Read(ref _supersededFrames),
            RejectedFrames: Interlocked.Read(ref _rejectedFrames),
            EncodedFrames: cadenceFrames,
            SubmittedFrames: submitted,
            CodedFrames: coded,
            EncodedFragments: encodedFragments,
            EncodedBytes: Interlocked.Read(ref _encodedBytes),
            DroppedFrames: Interlocked.Read(ref _droppedFrames),
            DeliveredFragments: Interlocked.Read(ref _deliveredFragments),
            PresentedFragments: presented,
            StaleFragments: Interlocked.Read(ref _staleFragments),
            RejectedFragments: Interlocked.Read(ref _rejectedFragments),
            MaximumQueueDepth: (int)Interlocked.Read(ref _maximumQueueDepth),
            MedianEncodeToPresentMs: median,
            P95EncodeToPresentMs: p95,
            RecoveryCount: (int)Interlocked.Read(ref _recoveryCount),
            LongestRecoveryMs: Math.Round(TimeSpan.FromTicks(Interlocked.Read(ref _longestRecoveryTicks)).TotalMilliseconds, 1),
            LastPresentedAt: lastPresented == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastPresented),
            // A source that is genuinely slower than the requested mode is not a
            // transport failure, but a GPU mode that cannot hold 30 FPS is
            // degraded and must be diagnosable rather than reported as success.
            DegradedCadence: GpuStreamStatusLabel.IsDegradedCadence(status, seconds, presentedFps),
            AverageReadbackMs: _averageReadbackMs,
            AverageSubmitMs: _averageSubmitMs,
            SlowestReadbackMs: _slowestReadbackMs,
            Note: note ?? Note);
    }

    private (double Median, double P95) Percentiles()
    {
        long[] samples;
        lock (_latencySamples)
        {
            if (_latencyCount == 0)
            {
                return (0, 0);
            }
            samples = new long[_latencyCount];
            Array.Copy(_latencySamples, samples, _latencyCount);
        }
        Array.Sort(samples);
        return (samples[samples.Length / 2], samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.95) - 1)]);
    }
}

public sealed record GpuStreamDiagnosticsSnapshot(
    string StreamId,
    string Status,
    string Transport,
    string EncoderMode,
    string DecoderMode,
    int CaptureWidth,
    int CaptureHeight,
    int RequestedFrameRate,
    double ObservedCapturedFps,
    double ObservedEncodedFps,
    double ObservedPresentedFps,
    long CapturedFrames,
    long PublishedFrames,
    long SupersededFrames,
    long RejectedFrames,
    long EncodedFrames,
    long SubmittedFrames,
    long CodedFrames,
    long EncodedFragments,
    long EncodedBytes,
    long DroppedFrames,
    long DeliveredFragments,
    long PresentedFragments,
    long StaleFragments,
    long RejectedFragments,
    int MaximumQueueDepth,
    double MedianEncodeToPresentMs,
    double P95EncodeToPresentMs,
    int RecoveryCount,
    double LongestRecoveryMs,
    DateTimeOffset? LastPresentedAt,
    bool DegradedCadence,
    double AverageReadbackMs,
    double AverageSubmitMs,
    double SlowestReadbackMs,
    string? Note)
{
    /// <summary>Compact single-line form for the controller window and Doctor output.</summary>
    public string Describe()
    {
        var note = string.IsNullOrWhiteSpace(Note) ? string.Empty : $" note={Note}";
        return $"{Status}; transport={Transport}; capture={CaptureWidth}x{CaptureHeight}; "
            + $"fps requested={RequestedFrameRate} captured={ObservedCapturedFps} encoded={ObservedEncodedFps} presented={ObservedPresentedFps}; "
            + $"frames captured={CapturedFrames} published={PublishedFrames} superseded={SupersededFrames} "
            + $"rejected={RejectedFrames} dropped={DroppedFrames} submitted={SubmittedFrames} coded={CodedFrames} bytes={EncodedBytes}; "
            + $"fragments encoded={EncodedFragments} delivered={DeliveredFragments} presented={PresentedFragments} stale={StaleFragments} rejected={RejectedFragments}; "
            + $"queueMax={MaximumQueueDepth}; latency median={MedianEncodeToPresentMs}ms p95={P95EncodeToPresentMs}ms; "
            + $"encoder={EncoderMode} decoder={DecoderMode}; recoveries={RecoveryCount} longestRecovery={LongestRecoveryMs}ms; "
            + $"frameCost wait={AverageReadbackMs}ms (slowest {SlowestReadbackMs}ms) submit={AverageSubmitMs}ms; "
            + $"stream={StreamId}{(DegradedCadence ? "; degraded" : string.Empty)}{note}";
    }
}
