using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

internal sealed record GpuEncoderOptions(
    int Width,
    int Height,
    int FrameRate,
    int BitrateBitsPerSecond,
    // Windows Graphics Capture returns rows top-down; the encoder input is
    // declared accordingly through MF_MT_DEFAULT_STRIDE.
    bool TopDownRows,
    TimeSpan MinimumFragmentDuration,
    // Hardware first is the product goal. The switches exist so a diagnostics run
    // on an unfamiliar machine can prove which part of the pipeline is the
    // obstacle instead of guessing.
    bool PreferHardware = true,
    bool LowLatency = true,
    bool HintFragmentDuration = true);

/// <summary>
/// Encodes captured BGRA frames to hardware-accelerated H.264 inside a
/// fragmented MP4 container and hands the resulting Media Source Extensions
/// units to the caller.
///
/// The container is written to an in-process <see cref="IMFByteStream"/>
/// implementation instead of a file: no temporary media ever reaches the disk,
/// nothing has to be watched for growth, and a slow consumer applies
/// backpressure through the byte stream rather than growing a queue.
/// </summary>
internal sealed class MediaFoundationH264Encoder : IDisposable
{
    /// <summary>Chunks that may wait for the transport before the stream is declared stalled and restarted.</summary>
    private const int MaximumQueuedChunks = 12;

    private readonly GpuEncoderOptions _options;
    private readonly MediaFoundationByteStream _byteStream;
    private readonly MfSinkWriter _writer;
    private readonly int _streamIndex;
    private readonly int _rowBytes;
    private readonly object _gate = new();
    private readonly Queue<Mp4Chunk> _chunks = new();
    private DateTimeOffset? _origin;
    private long _submittedFrames;
    private long _lastSubmittedFrameTicks;
    private bool _finalized;
    private bool _disposed;
    private string? _failure;

    private MediaFoundationH264Encoder(
        GpuEncoderOptions options,
        MediaFoundationByteStream byteStream,
        MfSinkWriter writer,
        int streamIndex)
    {
        _options = options;
        _byteStream = byteStream;
        _writer = writer;
        _streamIndex = streamIndex;
        _rowBytes = checked(options.Width * 4);
    }

    public string EncoderMode { get; private set; } = "unknown";

    public bool HasFailed => Volatile.Read(ref _failure) is not null;

    public string? FailureReason => Volatile.Read(ref _failure);

    public long SubmittedFrames => Interlocked.Read(ref _submittedFrames);

    /// <summary>Compact byte-stream counters, used by diagnostics and the smoke test.</summary>
    public string StreamDiagnostics => _byteStream.Describe();

    public int PendingChunks
    {
        get
        {
            lock (_gate)
            {
                return _chunks.Count;
            }
        }
    }

    /// <summary>
    /// Creates the encoder, or reports why the GPU media path is unavailable so
    /// the caller can select the compatibility backend with a real reason
    /// instead of a generic failure.
    /// </summary>
    public static bool TryCreate(
        GpuEncoderOptions options,
        out MediaFoundationH264Encoder? encoder,
        out string failure)
    {
        encoder = null;
        if (options.Width < 64 || options.Height < 64
            || options.Width > 4096 || options.Height > 4096
            || (long)options.Width * options.Height > 10_000_000)
        {
            failure = "The requested GPU encode size is outside the supported window.";
            return false;
        }
        if (options.FrameRate is < GpuStreamStatusLabel.MinimumGpuFrameRate or > GpuStreamStatusLabel.TargetFrameRate)
        {
            failure = $"The GPU media path supports {GpuStreamStatusLabel.MinimumGpuFrameRate} to "
                + $"{GpuStreamStatusLabel.TargetFrameRate} frames per second.";
            return false;
        }
        if (options.BitrateBitsPerSecond < 500_000 || options.BitrateBitsPerSecond > 80_000_000)
        {
            failure = "The requested GPU encode bitrate is outside the supported window.";
            return false;
        }

        var byteStream = new MediaFoundationByteStream();
        IntPtr writerAttributes = IntPtr.Zero;
        MfSinkWriter? writer = null;
        IntPtr outputType = IntPtr.Zero;
        IntPtr inputType = IntPtr.Zero;
        try
        {
            writer = MediaFoundationInterop.CreateSinkWriter(
                byteStream.InterfacePointer,
                options.MinimumFragmentDuration,
                options.PreferHardware,
                options.LowLatency,
                options.HintFragmentDuration,
                out writerAttributes,
                out failure);
            if (writer is null)
            {
                return false;
            }

            if (!TryConfigureTypes(writer, options, out outputType, out inputType, out var streamIndex, out failure))
            {
                return false;
            }

            var hr = writer.BeginWriting();
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                failure = $"The Media Foundation encoder pipeline could not start (0x{hr:X8}).";
                return false;
            }

            var instance = new MediaFoundationH264Encoder(options, byteStream, writer, streamIndex);
            instance.EncoderMode = instance.DetectEncoderMode();
            byteStream.Attach(instance);
            encoder = instance;
            failure = string.Empty;
            writer = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
        finally
        {
            if (inputType != IntPtr.Zero) Marshal.Release(inputType);
            if (outputType != IntPtr.Zero) Marshal.Release(outputType);
            writer?.Dispose();
            if (writerAttributes != IntPtr.Zero) Marshal.Release(writerAttributes);
            if (encoder is null)
            {
                byteStream.Dispose();
            }
        }
    }

    private static bool TryConfigureTypes(
        MfSinkWriter writer,
        GpuEncoderOptions options,
        out IntPtr outputType,
        out IntPtr inputType,
        out int streamIndex,
        out string failure)
    {
        outputType = IntPtr.Zero;
        inputType = IntPtr.Zero;
        streamIndex = -1;

        var hr = MediaFoundationInterop.MFCreateMediaType(out outputType);
        if (!MediaFoundationInterop.Succeeded(hr))
        {
            failure = $"The H.264 output media type could not be created (0x{hr:X8}).";
            return false;
        }
        ConfigureVideoType(outputType, options, MediaFoundationInterop.VideoFormatH264);
        MediaFoundationInterop.SetAttributeUInt32(
            outputType, MediaFoundationInterop.AverageBitrate, (uint)options.BitrateBitsPerSecond);
        MediaFoundationInterop.SetAttributeUInt32(
            outputType, MediaFoundationInterop.Mpeg2Profile, MediaFoundationInterop.H264ProfileHigh);
        MediaFoundationInterop.SetAttributeUInt32(
            outputType, MediaFoundationInterop.MaximumKeyframeSpacing, (uint)(options.FrameRate * 2));

        hr = writer.AddStream(outputType, out streamIndex);
        if (!MediaFoundationInterop.Succeeded(hr))
        {
            failure = $"Media Foundation rejected the H.264 output stream (0x{hr:X8}). "
                + "No H.264 encoder is available for the configured resolution.";
            return false;
        }

        hr = MediaFoundationInterop.MFCreateMediaType(out inputType);
        if (!MediaFoundationInterop.Succeeded(hr))
        {
            failure = $"The raw input media type could not be created (0x{hr:X8}).";
            return false;
        }
        // RGB32 keeps the pipeline free of a hand-written colour conversion:
        // Media Foundation inserts a video processor MFT that converts to NV12 on
        // the GPU where the hardware encoder requires it.
        ConfigureVideoType(inputType, options, MediaFoundationInterop.VideoFormatRgb32);
        MediaFoundationInterop.SetAttributeUInt32(inputType, MediaFoundationInterop.AllSamplesIndependent, 1);
        MediaFoundationInterop.SetAttributeUInt32(inputType, MediaFoundationInterop.DefaultStride,
            unchecked((uint)(options.TopDownRows ? -options.Width * 4 : options.Width * 4)));

        hr = writer.SetInputMediaType(streamIndex, inputType, IntPtr.Zero);
        if (!MediaFoundationInterop.Succeeded(hr))
        {
            // The frames are never silently reinterpreted in another colour
            // space: a swapped red/blue channel is worse than an honest fallback.
            failure = $"Media Foundation rejected the raw BGRA input type (0x{hr:X8}).";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static void ConfigureVideoType(IntPtr mediaType, GpuEncoderOptions options, Guid subType)
    {
        MediaFoundationInterop.SetAttributeGuid(
            mediaType, MediaFoundationInterop.MajorType, MediaFoundationInterop.MediaTypeVideo);
        MediaFoundationInterop.SetAttributeGuid(mediaType, MediaFoundationInterop.Subtype, subType);
        MediaFoundationInterop.SetAttributeUInt64(mediaType, MediaFoundationInterop.FrameSize,
            ((ulong)(uint)options.Width << 32) | (uint)options.Height);
        MediaFoundationInterop.SetAttributeUInt64(mediaType, MediaFoundationInterop.FrameRate,
            ((ulong)(uint)options.FrameRate << 32) | 1u);
        MediaFoundationInterop.SetAttributeUInt64(
            mediaType, MediaFoundationInterop.PixelAspectRatio, (1UL << 32) | 1u);
        MediaFoundationInterop.SetAttributeUInt32(
            mediaType, MediaFoundationInterop.InterlaceMode, MediaFoundationInterop.VideoInterlaceProgressive);
    }

    /// <summary>
    /// Reports whether the encoder Media Foundation actually instantiated is an
    /// asynchronous MFT. Windows hardware H.264 encoders are asynchronous and the
    /// inbox software encoder is not, so this distinguishes a real GPU encoder
    /// from a silent software fallback instead of trusting the requested
    /// attributes.
    /// </summary>
    private string DetectEncoderMode()
    {
        IntPtr transform = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            var service = Guid.Empty;
            var interfaceId = MediaFoundationInterop.TransformInterfaceId;
            var hr = _writer.GetServiceForStream(_streamIndex, ref service, ref interfaceId, out transform);
            if (!MediaFoundationInterop.Succeeded(hr) || transform == IntPtr.Zero)
            {
                return "unknown";
            }
            attributes = MediaFoundationInterop.TryGetTransformAttributes(transform);
            if (attributes == IntPtr.Zero)
            {
                return "unknown";
            }
            return MediaFoundationInterop.TryGetAttributeUInt32(
                attributes, MediaFoundationInterop.TransformAsync, out var isAsync)
                ? (isAsync != 0 ? "hardware" : "software")
                : "unknown";
        }
        catch
        {
            return "unknown";
        }
        finally
        {
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
            if (transform != IntPtr.Zero) Marshal.Release(transform);
        }
    }

    /// <summary>
    /// Submits one BGRA frame. Rows are copied with the orientation declared to
    /// the encoder through <c>MF_MT_DEFAULT_STRIDE</c>.
    /// </summary>
    public bool TrySubmitFrame(byte[] pixels, DateTimeOffset now, out string failure)
    {
        failure = string.Empty;
        ArgumentNullException.ThrowIfNull(pixels);
        if (HasFailed)
        {
            failure = FailureReason ?? "The encoder already failed.";
            return false;
        }
        if (pixels.Length < _rowBytes * _options.Height)
        {
            failure = "The captured frame was smaller than the configured encode size.";
            return false;
        }

        IntPtr buffer = IntPtr.Zero;
        IntPtr sample = IntPtr.Zero;
        try
        {
            var hr = MediaFoundationInterop.MFCreateMemoryBuffer((uint)pixels.Length, out buffer);
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                Fail($"A Media Foundation sample buffer could not be allocated (0x{hr:X8}).");
                failure = FailureReason!;
                return false;
            }
            hr = MediaFoundationInterop.LockBuffer(buffer, out var destination, out _, out _);
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                Fail($"A Media Foundation sample buffer could not be locked (0x{hr:X8}).");
                failure = FailureReason!;
                return false;
            }
            try
            {
                // The sample buffer is linear and top-down as far as Media
                // Foundation is concerned; the incoming rows are reordered when
                // the declared stride says the encoder wants them the other way.
                for (var row = 0; row < _options.Height; row++)
                {
                    var sourceRow = _options.TopDownRows ? row : _options.Height - 1 - row;
                    var targetRow = _options.TopDownRows ? _options.Height - 1 - row : row;
                    Marshal.Copy(pixels, sourceRow * _rowBytes, IntPtr.Add(destination, targetRow * _rowBytes), _rowBytes);
                }
            }
            finally
            {
                MediaFoundationInterop.UnlockBuffer(buffer);
            }
            MediaFoundationInterop.SetBufferLength(buffer, (uint)pixels.Length);

            hr = MediaFoundationInterop.MFCreateSample(out sample);
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                Fail($"A Media Foundation sample could not be created (0x{hr:X8}).");
                failure = FailureReason!;
                return false;
            }
            MediaFoundationInterop.AddSampleBuffer(sample, buffer);

            var timestamp = ToMediaTime(now);
            MediaFoundationInterop.SetSampleTime(sample, timestamp);
            MediaFoundationInterop.SetSampleDuration(sample, 10_000_000L / _options.FrameRate);

            Interlocked.Exchange(ref _lastSubmittedFrameTicks, timestamp);
            hr = _writer.WriteSample(_streamIndex, sample);
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                Fail($"The encoder rejected a frame (0x{hr:X8}).");
                failure = FailureReason!;
                return false;
            }
            Interlocked.Increment(ref _submittedFrames);
            return true;
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
            failure = exception.Message;
            return false;
        }
        finally
        {
            if (sample != IntPtr.Zero) Marshal.Release(sample);
            if (buffer != IntPtr.Zero) Marshal.Release(buffer);
        }
    }

    /// <summary>Media Foundation presents presentation times in 100-nanosecond units from the first submitted frame.</summary>
    private long ToMediaTime(DateTimeOffset now)
    {
        _origin ??= now;
        return (long)((now - _origin.Value).TotalSeconds * 10_000_000);
    }

    /// <summary>
    /// Removes the next Media Source Extensions unit, waiting up to
    /// <paramref name="timeoutMilliseconds"/> for the encoder to produce one. A
    /// false return without a failure means the encoder is still buffering:
    /// fragment boundaries belong to Media Foundation, not to this class.
    /// </summary>
    public bool TryTakeChunk(out Mp4Chunk? chunk, int timeoutMilliseconds)
    {
        chunk = null;
        var started = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while (_chunks.Count == 0)
            {
                if (_disposed || HasFailed)
                {
                    return false;
                }
                var remaining = timeoutMilliseconds - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remaining <= 0)
                {
                    return false;
                }
                Monitor.Wait(_gate, Math.Min(remaining, 250));
            }
            chunk = _chunks.Dequeue();
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    internal long LastSubmittedFrameTicks => Interlocked.Read(ref _lastSubmittedFrameTicks);

    /// <summary>Called from a Media Foundation thread when the byte stream becomes unusable.</summary>
    internal void FailFromStream(string reason) => Fail(reason);

    private void Fail(string reason) => Interlocked.CompareExchange(ref _failure, reason, null);

    /// <summary>Called by the byte stream on a Media Foundation thread.</summary>
    internal void OnChunk(Mp4Chunk chunk)
    {
        lock (_gate)
        {
            while (_chunks.Count >= MaximumQueuedChunks && !_disposed && !HasFailed)
            {
                // The transport is behind. Waiting here is deliberate: it
                // throttles capture and encode instead of accumulating latency,
                // and the timeout turns a permanently stalled consumer into a
                // diagnosable stream restart rather than a hang.
                if (!Monitor.Wait(_gate, 2000))
                {
                    Fail($"The media transport did not drain within 2 seconds with {MaximumQueuedChunks} fragments queued.");
                    return;
                }
            }
            if (_disposed || HasFailed)
            {
                return;
            }
            _chunks.Enqueue(chunk);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Ends the input stream so Media Foundation flushes the encoder and the
    /// final fragment. The encoder stays usable for draining afterwards, which is
    /// what lets a test prove the last fragment is a well-formed one.
    /// </summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finalized)
            {
                return;
            }
            _finalized = true;
        }
        try
        {
            _writer.FinalizeWriter();
        }
        catch
        {
            // A partially written fragmented stream is discarded together with
            // its session; a finalisation failure must not mask the real cause.
        }
    }

    public void Dispose()
    {
        Finish();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
        _writer.Dispose();
        _byteStream.Dispose();
        lock (_gate)
        {
            _chunks.Clear();
        }
    }
}

/// <summary>
/// Managed <see cref="IMFByteStream"/> that Media Foundation writes the
/// fragmented MP4 container into. Only the write path is real: the stream
/// advertises itself as forward-only and non-seekable, so the MPEG-4 sink emits
/// its initialisation segment before the media fragments and never tries to
/// back-patch a header that a live reader has already consumed.
/// </summary>
internal sealed class MediaFoundationByteStream : IMFByteStream, IDisposable
{
    private readonly object _gate = new();
    private readonly Mp4ChunkAssembler _assembler = new();
    private readonly IntPtr _interfacePointer;
    private MediaFoundationH264Encoder? _owner;
    private ulong _length;
    private int _writeCalls;
    private int _beginWriteCalls;
    private int _endWriteCalls;
    private int _readCalls;
    private int _beginReadCalls;
    private int _seekCalls;
    private int _setPositionCalls;
    private int _getLengthCalls;
    private int _setLengthCalls;
    private int _capabilityCalls;
    private int _flushCalls;
    private uint _lastAsyncWritten;
    private bool _disposed;

    public MediaFoundationByteStream()
    {
        // Media Foundation is handed a pointer to this object's COM callable
        // wrapper, so no marshalling shim and no file system are involved.
        _interfacePointer = Marshal.GetComInterfaceForObject(this, typeof(IMFByteStream));
    }

    internal IntPtr InterfacePointer => _interfacePointer;

    /// <summary>
    /// Call counters. Media Foundation's fragmented MP4 sink is free to use
    /// either the synchronous or the callback pair for I/O, and which one it
    /// picks determines whether this stream can support it at all, so the
    /// counters are reported instead of guessed at.
    /// </summary>
    internal string Describe()
    {
        lock (_gate)
        {
            return $"byteStream: write={_writeCalls} beginWrite={_beginWriteCalls} endWrite={_endWriteCalls} "
                + $"read={_readCalls} beginRead={_beginReadCalls} seek={_seekCalls} setPos={_setPositionCalls} "
                + $"getLen={_getLengthCalls} setLen={_setLengthCalls} capabilities={_capabilityCalls} "
                + $"flush={_flushCalls} bytes={_length} assembler={(HasFailed ? _assembler.FailureReason : "ok")} "
                + $"pending={_assembler.PendingBytes} initEmitted={_assembler.InitEmitted}";
        }
    }

    internal int WriteCalls
    {
        get
        {
            lock (_gate)
            {
                return _writeCalls;
            }
        }
    }

    internal long BytesWritten
    {
        get
        {
            lock (_gate)
            {
                return (long)_length;
            }
        }
    }

    private bool HasFailed => _assembler.HasFailed;

    internal void Attach(MediaFoundationH264Encoder owner) => _owner = owner;

    public int GetCapabilities(out uint capabilities)
    {
        Interlocked.Increment(ref _capabilityCalls);
        capabilities = MediaFoundationInterop.ByteStreamIsWritable;
        return MediaFoundationInterop.S_OK;
    }

    public int GetLength(out ulong length)
    {
        Interlocked.Increment(ref _getLengthCalls);
        lock (_gate)
        {
            length = _length;
        }
        return MediaFoundationInterop.S_OK;
    }

    public int SetLength(ulong length)
    {
        Interlocked.Increment(ref _setLengthCalls);
        lock (_gate)
        {
            _length = length;
        }
        return MediaFoundationInterop.S_OK;
    }

    public int GetCurrentPosition(out ulong position)
    {
        lock (_gate)
        {
            position = _length;
        }
        return MediaFoundationInterop.S_OK;
    }

    public int SetCurrentPosition(ulong position)
    {
        Interlocked.Increment(ref _setPositionCalls);
        return MediaFoundationInterop.E_NOTIMPL;
    }

    public int IsEndOfStream(out int endOfStream)
    {
        endOfStream = 0;
        return MediaFoundationInterop.S_OK;
    }

    public int Read(IntPtr buffer, uint count, out uint read)
    {
        Interlocked.Increment(ref _readCalls);
        read = 0;
        return MediaFoundationInterop.E_NOTIMPL;
    }

    public int BeginRead(IntPtr buffer, uint count, IntPtr callback, IntPtr state)
    {
        Interlocked.Increment(ref _beginReadCalls);
        return MediaFoundationInterop.E_NOTIMPL;
    }

    public int EndRead(IntPtr result, out uint read)
    {
        read = 0;
        return MediaFoundationInterop.E_NOTIMPL;
    }

    public int Write(IntPtr buffer, uint count, out uint written)
    {
        written = 0;
        Interlocked.Increment(ref _writeCalls);
        if (count == 0)
        {
            return MediaFoundationInterop.S_OK;
        }
        lock (_gate)
        {
            if (_disposed || buffer == IntPtr.Zero)
            {
                return MediaFoundationInterop.E_UNEXPECTED;
            }
        }
        try
        {
            var bytes = new byte[count];
            Marshal.Copy(buffer, bytes, 0, checked((int)count));
            var chunks = _assembler.Append(bytes, _owner?.LastSubmittedFrameTicks ?? 0, DateTimeOffset.UtcNow);
            lock (_gate)
            {
                _length += count;
            }
            written = count;
            if (_assembler.HasFailed)
            {
                _owner?.FailFromStream(_assembler.FailureReason ?? "The fragmented MP4 container was malformed.");
                return MediaFoundationInterop.S_OK;
            }
            foreach (var chunk in chunks)
            {
                _owner?.OnChunk(chunk);
            }
            return MediaFoundationInterop.S_OK;
        }
        catch (Exception exception)
        {
            _owner?.FailFromStream(exception.Message);
            return MediaFoundationInterop.E_FAIL;
        }
    }

    public int BeginWrite(IntPtr buffer, uint count, IntPtr callback, IntPtr state)
    {
        Interlocked.Increment(ref _beginWriteCalls);
        // The write itself is immediate. Media Foundation's asynchronous contract
        // additionally requires the completion callback to be invoked before the
        // caller may use EndWrite, which the adaptor below does.
        var result = Write(buffer, count, out var written);
        if (callback != IntPtr.Zero)
        {
            _lastAsyncWritten = written;
            MediaFoundationInterop.CompleteAsyncCallback(callback, state, result);
        }
        return result;
    }

    public int EndWrite(IntPtr result, out uint written)
    {
        Interlocked.Increment(ref _endWriteCalls);
        written = _lastAsyncWritten;
        return MediaFoundationInterop.S_OK;
    }

    public int Seek(int seekOrigin, long seekOffset, uint seekFlags, out ulong position)
    {
        Interlocked.Increment(ref _seekCalls);
        lock (_gate)
        {
            position = _length;
        }
        return MediaFoundationInterop.E_NOTIMPL;
    }

    public int Flush()
    {
        Interlocked.Increment(ref _flushCalls);
        return MediaFoundationInterop.S_OK;
    }

    public int Close()
    {
        Dispose();
        return MediaFoundationInterop.S_OK;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        if (_interfacePointer != IntPtr.Zero)
        {
            Marshal.Release(_interfacePointer);
        }
    }
}
