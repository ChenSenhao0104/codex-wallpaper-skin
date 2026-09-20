namespace CodexWallpaperSkin;

/// <summary>
/// The v0.4 production media path for one Wallpaper Engine surface.
///
/// Capture (Windows Graphics Capture, D3D11) and encode (Media Foundation
/// hardware H.264 into a fragmented MP4) are owned here; the transport and the
/// presentation surface are owned by the caller, which pushes the batches into
/// the page. The pipeline holds exactly one immutable stream identity, so a late
/// frame from a previous wallpaper can never enter a replacement stream.
/// </summary>
internal sealed class GpuMediaPipeline : IDisposable
{
    /// <summary>Name reported in diagnostics; no socket is opened by this transport.</summary>
    public const string TransportName = "cdp-fragment";

    private const int MaximumCaptureTimeoutSeconds = 4;
    private const int FragmentBatchFrames = 4;
    private const int FragmentBatchBytes = 384 * 1024;
    private static readonly TimeSpan FragmentBatchDelay = TimeSpan.FromMilliseconds(60);

    private readonly WindowsGraphicsCaptureSource _capture;
    private readonly MediaFoundationH264Encoder _encoder;
    private readonly GpuStreamBatcher _batcher = new(FragmentBatchFrames, FragmentBatchBytes, FragmentBatchDelay);
    private bool _disposed;

    private GpuMediaPipeline(
        WindowsGraphicsCaptureSource capture,
        MediaFoundationH264Encoder encoder,
        string streamId,
        int width,
        int height,
        int frameRate,
        byte[] firstFramePixels)
    {
        _capture = capture;
        _encoder = encoder;
        StreamId = streamId;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        _firstFramePixels = firstFramePixels;
        Diagnostics = new GpuStreamDiagnostics(streamId, TransportName, width, height, frameRate)
        {
            EncoderMode = encoder.EncoderMode
        };
    }

    /// <summary>Immutable identity for this pipeline; every fragment carries it.</summary>
    public string StreamId { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameRate { get; }

    /// <summary>Exact Media Source Extensions codec string, available once the initialisation segment has been read.</summary>
    public string? Codec { get; private set; }

    /// <summary>
    /// Hands over the first captured frame once, so the caller can paint an
    /// immediate still image while the video surface initialises. The buffer is
    /// released on the first call to keep the pipeline's memory bounded.
    /// </summary>
    public byte[]? TakeFirstFramePixels()
    {
        var pixels = _firstFramePixels;
        _firstFramePixels = null;
        return pixels;
    }

    private byte[]? _firstFramePixels;

    public GpuStreamDiagnostics Diagnostics { get; }

    public bool UsesHardwareEncoder => _encoder.EncoderMode == "hardware";

    public long SubmittedFrames => _encoder.SubmittedFrames;

    public string? FailureReason => _encoder.FailureReason ?? _captureFailure;

    public bool HasFailed => _encoder.HasFailed || _captureFailure is not null;

    public int PendingFragments => _encoder.PendingChunks;

    private string? _captureFailure;

    /// <summary>
    /// Starts the GPU path, or reports precisely why it is unavailable so the
    /// caller can select the reduced-frame-rate compatibility backend with a real
    /// reason instead of a generic failure.
    /// </summary>
    public static bool TryStart(
        IntPtr window,
        WallpaperSettings settings,
        out GpuMediaPipeline? pipeline,
        out string failure)
    {
        pipeline = null;
        ArgumentNullException.ThrowIfNull(settings);

        var capture = WindowsGraphicsCaptureSource.TryStartRaw(window, out failure);
        if (capture is null)
        {
            return false;
        }

        MediaFoundationH264Encoder? encoder = null;
        try
        {
            if (!MediaFoundationInterop.TryStartup(out failure))
            {
                return false;
            }
            if (!WaitForFirstFrame(capture, out var width, out var height, out var firstFrame, out failure))
            {
                return false;
            }

            // The frame rate declared to the encoder must be one the machine can
            // actually feed. Media Foundation fills a declared rate by repeating
            // frames when input arrives more slowly, which would let the product
            // claim 60 FPS while the user sees about 17 unique frames per second.
            // The capture cadence is therefore measured first and the honest rate
            // is declared, capped by what the user asked for.
            var requestedFrameRate = GpuStreamStatusLabel.NormalizeFrameRate(settings.SceneFrameRate);
            var measuredFrameRate = MeasureCaptureFrameRate(capture, requestedFrameRate);
            var frameRate = GpuStreamStatusLabel.AlignFrameRate(measuredFrameRate, requestedFrameRate);

            var bitrate = Math.Clamp(
                (int)(width * (long)height * frameRate * 0.09),
                3_000_000,
                25_000_000);
            var options = new GpuEncoderOptions(
                width,
                height,
                frameRate,
                bitrate,
                TopDownRows: true,
                MinimumFragmentDuration: TimeSpan.FromMilliseconds(100));
            if (!MediaFoundationH264Encoder.TryCreate(options, out encoder, out failure) || encoder is null)
            {
                return false;
            }

            var instance = new GpuMediaPipeline(
                capture, encoder, Guid.NewGuid().ToString("N"), width, height, frameRate, firstFrame!);
            // The first frame is submitted so the encoder writes the
            // initialisation segment immediately: the page cannot create its
            // Media Source buffer until ftyp and moov have arrived. Media
            // Foundation finalises a fragment only when the next frame arrives,
            // so priming keeps feeding frames until the initialisation segment
            // actually appears instead of assuming one frame is enough.
            if (!instance.Submit(firstFrame!, out failure)
                || !instance.PrimeForInitialisationSegment(TimeSpan.FromSeconds(5), out failure))
            {
                instance.Dispose();
                return false;
            }
            pipeline = instance;
            encoder = null;
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
        finally
        {
            encoder?.Dispose();
            if (pipeline is null)
            {
                capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>
    /// Measures how many frames per second the capture source can actually
    /// deliver, so the encoder can be told the truth instead of a rate that
    /// Media Foundation would fill by repeating frames.
    /// </summary>
    private static int MeasureCaptureFrameRate(WindowsGraphicsCaptureSource capture, int requestedFrameRate)
    {
        var window = TimeSpan.FromMilliseconds(600);
        var started = capture.PublishedRawFrames;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new byte[1];
        while (clock.Elapsed < window)
        {
            // Drain the channel so the producer keeps publishing, but do not feed
            // the encoder yet: this measurement precedes its creation.
            capture.TryReadRawFrame(40, out _, out _, out _);
        }
        var published = capture.PublishedRawFrames - started;
        var seconds = Math.Max(0.05, clock.Elapsed.TotalSeconds);
        var measured = (int)Math.Round(published / seconds);
        return measured <= 0 ? requestedFrameRate : measured;
    }

    private static bool WaitForFirstFrame(
        WindowsGraphicsCaptureSource capture,
        out int width,
        out int height,
        out byte[]? frame,
        out string failure)
    {
        width = 0;
        height = 0;
        frame = null;
        failure = string.Empty;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(MaximumCaptureTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!capture.TryReadRawFrame(250, out var candidate, out var candidateWidth, out var candidateHeight)
                || candidate is null)
            {
                continue;
            }
            if (!CapturedFrameQuality.IsAcceptable(candidate, candidateWidth, candidateHeight))
            {
                continue;
            }
            width = candidateWidth;
            height = candidateHeight;
            frame = candidate;
            return true;
        }
        failure = "Windows Graphics Capture produced no complete, usable frame within 4 seconds.";
        return false;
    }

    /// <summary>
    /// Moves encoder output into the transport batcher. Returns false when the
    /// caller must stop: either the pipeline failed or nothing is available yet.
    /// </summary>
    public bool DrainFragments(int waitMilliseconds, out string failure)
    {
        failure = string.Empty;
        if (_disposed)
        {
            failure = "The GPU media pipeline is closed.";
            return false;
        }
        var drained = false;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, waitMilliseconds));
        while (true)
        {
            var remaining = (int)Math.Max(0, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds);
            if (!_encoder.TryTakeChunk(out var chunk, drained ? 0 : remaining) || chunk is null)
            {
                break;
            }
            if (chunk.Kind == Mp4ChunkKind.Init && Codec is null
                && Mp4StreamInspector.TryReadAvcCodecString(chunk.Bytes, out var codec))
            {
                // The exact codec string is read from the stream Media Foundation
                // produced rather than guessed, so the page never advertises a
                // profile the encoder did not write.
                Codec = codec;
            }
            _batcher.Add(chunk);
            drained = true;
            Diagnostics.CountEncodedFragment(chunk.Bytes.Length);
            // The coded frame count comes from the fragment's own table: Media
            // Foundation may repeat frames to fill the declared rate, so this is
            // the only honest measure of the stream's cadence.
            if (Mp4StreamInspector.TryReadFragmentSampleCount(chunk.Bytes, out var codedFrames))
            {
                Diagnostics.CountCodedFrames(codedFrames);
            }
        }
        Diagnostics.ObserveQueueDepth(PendingFragments);
        Diagnostics.SetSubmittedFrames(_encoder.SubmittedFrames);
        if (HasFailed)
        {
            failure = FailureReason ?? "The GPU media pipeline failed.";
            return false;
        }
        return drained;
    }

    public bool HasPendingBatch => _batcher.Count > 0;

    public bool ShouldFlushBatch(DateTimeOffset now) => _batcher.ShouldFlush(now);

    public GpuFrameBatch? TakeBatch(DateTimeOffset now) => _batcher.Take(now);

    /// <summary>Records the outcome of one transport message and the page's acknowledgement.</summary>
    public void ReportDelivery(GpuFrameBatch batch, string pageStatus, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        switch (pageStatus)
        {
            case "presented":
                Diagnostics.CountDeliveredFragment(batch.Count);
                Diagnostics.CountPresented(batch.Count);
                Diagnostics.RecordLatency(now - batch.CreatedAt);
                break;
            case "appended":
                Diagnostics.CountDeliveredFragment(batch.Count);
                break;
            case "stale":
                Diagnostics.CountStaleFragment();
                break;
            default:
                Diagnostics.CountRejectedFragment();
                break;
        }
    }

    public void CountCapturedFrame(bool accepted)
    {
        if (accepted)
        {
            Diagnostics.CountCaptured();
        }
        else
        {
            Diagnostics.CountRejected();
        }
    }

    public void CountDroppedFrame() => Diagnostics.CountDropped();

    public void CountRecovery(TimeSpan duration) => Diagnostics.CountRecovery(duration);

    public GpuStreamDiagnosticsSnapshot Snapshot(GpuStreamStatus status, string? note = null)
    {
        Diagnostics.SetSubmittedFrames(_encoder.SubmittedFrames);
        var published = _capture.PublishedRawFrames;
        var consumed = _capture.ConsumedRawFrames;
        Diagnostics.SetCaptureGauges(
            published,
            Math.Max(0, published - consumed - WindowsGraphicsCaptureSource.RawFrameCapacity));
        var (readMs, submitMs, slowestReadMs) = FrameTimings();
        Diagnostics.SetFrameTimings(readMs, submitMs, slowestReadMs);
        return Diagnostics.Snapshot(status, note);
    }

    /// <summary>Reads the newest captured frame and submits it to the encoder.</summary>
    public bool CaptureAndSubmit(int timeoutMilliseconds, out string failure)
    {
        failure = string.Empty;
        if (_disposed)
        {
            failure = "The GPU media pipeline is closed.";
            return false;
        }
        if (_encoder.HasFailed)
        {
            failure = _encoder.FailureReason ?? "The GPU media pipeline failed.";
            return false;
        }
        var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var hasFrame = _capture.TryReadRawFrame(timeoutMilliseconds, out var pixels, out var width, out var height);
        var readElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(readStarted);
        if (!hasFrame || pixels is null)
        {
            // A Scene may intentionally stop producing frames while it is
            // visually static, and a short read timeout simply means no new frame
            // has arrived. Neither is a dropped frame, and neither may tear the
            // stream down or degrade it to the compatibility backend.
            return true;
        }

        // A frame the bounded capture channel had to supersede never reached the
        // encoder. This is a gauge rather than a running total: the difference
        // between published and consumed already counts every superseded frame,
        // so accumulating it per read would multiply the same drop.
        var published = _capture.PublishedRawFrames;
        var consumed = _capture.ConsumedRawFrames;
        Diagnostics.SetCaptureGauges(
            published,
            Math.Max(0, published - consumed - WindowsGraphicsCaptureSource.RawFrameCapacity));
        if (width != Width || height != Height)
        {
            // A capture size change invalidates the encoder configuration, so the
            // caller is told to rebuild the stream rather than being fed frames
            // that would be silently stretched.
            _captureFailure = $"The Wallpaper Engine capture size changed to {width}x{height}; the stream must be rebuilt.";
            failure = _captureFailure;
            return false;
        }
        if (!CapturedFrameQuality.IsAcceptable(pixels, width, height))
        {
            CountCapturedFrame(accepted: false);
            return true;
        }
        CountCapturedFrame(accepted: true);
        var submitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var submitted = Submit(pixels, out failure);
        RecordFrameTiming(readElapsed, System.Diagnostics.Stopwatch.GetElapsedTime(submitStarted));
        return submitted;
    }

    private bool Submit(byte[] pixels, out string failure)
    {
        if (!_encoder.TrySubmitFrame(pixels, DateTimeOffset.UtcNow, out failure))
        {
            _captureFailure = failure;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Splits the per-frame cost so a throughput ceiling can be attributed instead
    /// of guessed: reading is the Windows Graphics Capture readback, submitting is
    /// the Media Foundation sample copy and encode hand-off.
    /// </summary>
    public void RecordFrameTiming(TimeSpan read, TimeSpan submit)
    {
        var readMs = read.TotalMilliseconds;
        var submitMs = submit.TotalMilliseconds;
        Interlocked.Add(ref _totalReadTicks, (long)(readMs * 1000));
        Interlocked.Add(ref _totalSubmitTicks, (long)(submitMs * 1000));
        Interlocked.Increment(ref _timedFrames);
        var current = Volatile.Read(ref _slowestReadMs);
        while (readMs > current)
        {
            var observed = Interlocked.CompareExchange(ref _slowestReadMs, readMs, current);
            if (observed == current) break;
            current = observed;
        }
    }

    private long _totalReadTicks;
    private long _totalSubmitTicks;
    private long _timedFrames;
    private double _slowestReadMs;

    private (double ReadMs, double SubmitMs, double SlowestReadMs) FrameTimings()
    {
        var frames = Interlocked.Read(ref _timedFrames);
        if (frames == 0)
        {
            return (0, 0, 0);
        }
        return (
            Interlocked.Read(ref _totalReadTicks) / 1000d / frames,
            Interlocked.Read(ref _totalSubmitTicks) / 1000d / frames,
            Volatile.Read(ref _slowestReadMs));
    }

    /// <summary>
    /// Feeds frames until the encoder has emitted a complete initialisation
    /// segment, or the budget expires. Bounded, so a machine whose encoder never
    /// produces a streamable header is diagnosed instead of hanging the apply.
    /// </summary>
    private bool PrimeForInitialisationSegment(TimeSpan budget, out string failure)
    {
        failure = string.Empty;
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            DrainFragments(0, out _);
            if (Codec is not null)
            {
                return true;
            }
            if (HasFailed)
            {
                failure = FailureReason ?? "The GPU media pipeline failed while priming the encoder.";
                return false;
            }
            // CaptureAndSubmit enforces the size and quality gates and is
            // harmless when the scene is momentarily static.
            CaptureAndSubmit(120, out _);
        }
        failure = "The encoder produced no streamable initialisation segment within "
            + $"{budget.TotalSeconds:F0} seconds.";
        return false;
    }

    public void Finish() => _encoder.Finish();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _encoder.Dispose();
        _capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
