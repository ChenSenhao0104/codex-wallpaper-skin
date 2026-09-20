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
        byte[] firstFramePixels,
        string? frameRateLimitReason = null)
    {
        _capture = capture;
        _encoder = encoder;
        StreamId = streamId;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        _firstFramePixels = firstFramePixels;
        FrameRateLimitReason = frameRateLimitReason;
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
    /// Set when the pipeline declined to declare the requested rate for a measured
    /// stability reason, so the caller can explain the fallback instead of
    /// attributing it to the source.
    /// </summary>
    public string? FrameRateLimitReason { get; private set; }

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

            // The declared frame rate is authoritative: Media Foundation resamples
            // the timeline to it, repeating frames when input is slower and
            // discarding them when it is faster. It is therefore measured from the
            // live source, after the Scene has finished initialising, and capped by
            // what the user asked for.
            var requestedFrameRate = GpuStreamStatusLabel.NormalizeFrameRate(settings.SceneFrameRate);
            var measuredFrameRate = MeasureCaptureFrameRate(capture, requestedFrameRate);
            var frameRate = GpuStreamStatusLabel.AlignFrameRate(measuredFrameRate, requestedFrameRate);

            // Measured on this machine: the Media Foundation hardware H.264 encoder
            // accepts 1920x1080 at 60 FPS for about 260 frames and then stops
            // accepting frames indefinitely, while 1920x1080 at 30 FPS is stable for
            // a whole run and the same 1080p60 configuration without the
            // hardware-transform preference was stable for a whole run too. Where
            // hardware is known to wedge, the software encoder carries the
            // resolution rather than the cadence being dropped: 60 FPS is the
            // product goal, and a labeled software encoder is a better answer than a
            // labeled 30 FPS stream. Priming cannot catch the wedge, because it needs
            // several seconds of sustained encoding to appear.
            var preferHardware = true;
            string? frameRateLimitReason = null;
            if (MediaFoundationH264Encoder.IsUnstableHardwareRate(width, height, frameRate))
            {
                preferHardware = false;
                frameRateLimitReason =
                    $"the hardware H.264 encoder is not stable at {width}x{height} above "
                    + $"{GpuStreamStatusLabel.FallbackFrameRate} FPS on this machine (it stops accepting frames after a "
                    + "few seconds), so the software encoder carries this resolution instead of dropping the cadence";
            }

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
                MinimumFragmentDuration: TimeSpan.FromMilliseconds(100),
                PreferHardware: preferHardware);
            if (!MediaFoundationH264Encoder.TryCreate(options, out encoder, out failure) || encoder is null)
            {
                return false;
            }

            var instance = new GpuMediaPipeline(
                capture, encoder, Guid.NewGuid().ToString("N"), width, height, frameRate, firstFrame!,
                frameRateLimitReason);
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
    /// Measures how many frames per second the live capture source can deliver, so
    /// the encoder can be declared a rate that matches it instead of one Media
    /// Foundation would fill with repeated frames.
    ///
    /// A Scene that has just started still compiles shaders and loads textures, so
    /// the first frames are deliberately discarded: measuring during initialisation
    /// under-reports the steady-state rate and would pin the whole session to a
    /// cadence the machine can beat.
    /// </summary>
    private static int MeasureCaptureFrameRate(WindowsGraphicsCaptureSource capture, int requestedFrameRate)
    {
        var settle = TimeSpan.FromMilliseconds(500);
        var window = TimeSpan.FromMilliseconds(1200);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < settle)
        {
            // Drain without counting, so the producer keeps publishing.
            capture.TryReadRawFrame(60, out _, out _, out _);
        }
        var publishedAtStart = capture.PublishedRawFrames;
        var measuredFrom = clock.Elapsed;
        while (clock.Elapsed - measuredFrom < window)
        {
            capture.TryReadRawFrame(60, out _, out _, out _);
        }
        var published = capture.PublishedRawFrames - publishedAtStart;
        var seconds = Math.Max(0.05, (clock.Elapsed - measuredFrom).TotalSeconds);
        var measured = (int)Math.Round(published / seconds);
        return measured <= 0 ? requestedFrameRate : measured;
    }

    /// <summary>
    /// Cadence of frames that really came from the source. This, not the coded
    /// frame count, is what a status claim may rest on.
    /// </summary>
    public double MeasuredSourceFps => Diagnostics.MeasuredSourceFps;

    /// <summary>Coded cadence, which includes frames Media Foundation repeated to fill the declared rate.</summary>
    public double MeasuredCodedFps => Diagnostics.MeasuredCodedFps;

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
            _submittedAtLastFragment = _encoder.SubmittedFrames;
            _lastFragmentAt = DateTimeOffset.UtcNow;
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
    /// Runs the priming loop on its own thread under a hard budget.
    ///
    /// Priming submits real frames, so a hardware encoder that blocks would
    /// otherwise hang the caller that is applying a wallpaper. A blocked call
    /// cannot be cancelled, so the thread is abandoned and the pipeline is marked
    /// stalled: the caller sees a clear failure and selects the compatibility
    /// backend instead of freezing.
    /// </summary>
    private bool PrimeForInitialisationSegment(TimeSpan budget, out string failure)
    {
        failure = string.Empty;
        var completed = new ManualResetEventSlim(false);
        var primeFailure = string.Empty;
        var worker = new Thread(() =>
        {
            try
            {
                primeFailure = PrimeCore(budget);
            }
            catch (Exception exception)
            {
                primeFailure = exception.Message;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = "cws-gpu-encoder-prime"
        };
        worker.Start();

        if (!completed.Wait(budget + TimeSpan.FromSeconds(3)))
        {
            _encoder.MarkStalled(
                $"the hardware encoder stopped accepting frames while priming {Width}x{Height}; "
                + "the resolution or the encoder configuration is not usable here");
            failure = _encoder.FailureReason ?? "The encoder stalled while priming.";
            return false;
        }
        failure = primeFailure;
        return primeFailure.Length == 0;
    }

    private string PrimeCore(TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            DrainFragments(0, out _);
            if (Codec is not null)
            {
                _submittedAtLastFragment = _encoder.SubmittedFrames;
                _lastFragmentAt = DateTimeOffset.UtcNow;
                return string.Empty;
            }
            if (HasFailed)
            {
                return FailureReason ?? "The GPU media pipeline failed while priming the encoder.";
            }
            // CaptureAndSubmit enforces the size and quality gates and is
            // harmless when the scene is momentarily static.
            CaptureAndSubmit(120, out _);
        }
        return "The encoder produced no streamable initialisation segment within "
            + $"{budget.TotalSeconds:F0} seconds.";
    }

    /// <summary>
    /// Detects an encoder that accepted frames and then stopped producing anything,
    /// which is how a blocked Media Foundation call presents itself from outside.
    /// Returns false once the pipeline must be abandoned.
    /// </summary>
    public bool CheckForStall(DateTimeOffset now, out string failure)
    {
        failure = string.Empty;
        if (_disposed || HasFailed)
        {
            failure = FailureReason ?? "The GPU media pipeline is closed.";
            return false;
        }
        var submittedSinceFragment = _encoder.SubmittedFrames - _submittedAtLastFragment;
        if (submittedSinceFragment >= MinimumSubmittedFramesForStallCheck
            && now - _lastFragmentAt > StallThreshold)
        {
            _encoder.MarkStalled(
                $"the hardware encoder stopped producing output after {submittedSinceFragment} accepted frames at "
                + $"{Width}x{Height}; the stream is being abandoned");
            failure = _encoder.FailureReason ?? "The encoder stalled.";
            return false;
        }
        return true;
    }

    /// <summary>Frames that must be accepted with no output before the encoder is called stalled.</summary>
    private const long MinimumSubmittedFramesForStallCheck = 3;

    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(4);
    private long _submittedAtLastFragment;
    private DateTimeOffset _lastFragmentAt = DateTimeOffset.UtcNow;

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
