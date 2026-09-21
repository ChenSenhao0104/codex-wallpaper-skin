using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace CodexWallpaperSkin;

internal sealed record H264EncodedFrame(
    byte[] Data,
    long TimestampMicroseconds,
    bool KeyFrame);

/// <summary>
/// Low-latency H.264 elementary-stream encoder backed by a Windows hardware
/// Media Foundation transform. Input is NV12 system memory for the first v0.4
/// milestone; the transport and browser decoder do not depend on this input
/// representation, so the subsequent D3D11 texture path can replace it without
/// another browser protocol change.
/// </summary>
internal sealed class MediaFoundationH264Encoder : IDisposable
{
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;
    private const int OutputStreamProvidesSamples = 0x00000100;
    private const int OutputStreamCanProvideSamples = 0x00000200;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameRate;
    private readonly int _outputBufferSize;
    private bool _needInput;
    private long _nextSampleTime;
    private bool _disposed;

    private MediaFoundationH264Encoder(
        IMFTransform transform,
        IMFMediaEventGenerator events,
        int width,
        int height,
        int frameRate,
        int outputBufferSize)
    {
        _transform = transform;
        _events = events;
        _width = width;
        _height = height;
        _frameRate = frameRate;
        _outputBufferSize = outputBufferSize;
    }

    public static MediaFoundationH264Encoder Create(int width, int height, int frameRate, int bitrate)
    {
        width &= ~1;
        height &= ~1;
        if (width is < 64 or > 4096 || height is < 64 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(width));
        frameRate = Math.Clamp(frameRate, 15, 60);
        bitrate = Math.Clamp(bitrate, 1_000_000, 40_000_000);

        MediaFactory.MFStartup().CheckError();
        IMFTransform? transform = null;
        IMFMediaEventGenerator? events = null;
        try
        {
            transform = ActivateHardwareEncoder();
            transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true).CheckError();

            using (var outputType = MediaFactory.MFCreateMediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, checked((uint)bitrate)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(frameRate, 1)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
                // Constrained Baseline keeps the elementary stream compatible
                // with Chromium WebCodecs across integrated and discrete GPUs.
                outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 66u).CheckError();
                transform.SetOutputType(0, outputType, 0);
            }
            using (var inputType = MediaFactory.MFCreateMediaType())
            {
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
                inputType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height)).CheckError();
                inputType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(frameRate, 1)).CheckError();
                inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1)).CheckError();
                inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
                inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, true).CheckError();
                transform.SetInputType(0, inputType, 0);
            }

            events = transform.QueryInterface<IMFMediaEventGenerator>();
            var outputInfo = transform.GetOutputStreamInfo(0);
            var outputBufferSize = Math.Max(outputInfo.Size, 2 * 1024 * 1024);
            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            return new MediaFoundationH264Encoder(transform, events, width, height, frameRate, outputBufferSize);
        }
        catch
        {
            events?.Dispose();
            transform?.Dispose();
            try { MediaFactory.MFShutdown().CheckError(); } catch { }
            throw;
        }
    }

    public IReadOnlyList<H264EncodedFrame> EncodeBgra(
        byte[] bgra,
        int sourceWidth,
        int sourceHeight,
        int sourceStride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceWidth < _width || sourceHeight < _height || sourceStride < sourceWidth * 4)
            throw new ArgumentException("The source frame is smaller than the configured encoder surface.", nameof(bgra));
        if (bgra.Length < checked(sourceStride * sourceHeight))
            throw new ArgumentException("The source frame buffer is incomplete.", nameof(bgra));

        var outputs = new List<H264EncodedFrame>(2);
        WaitUntilInputIsNeeded(outputs);
        var nv12 = ConvertBgraToNv12(bgra, _width, _height, sourceStride);
        using var sample = MediaFactory.MFCreateSample();
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length))
        {
            buffer.Lock(out var destination, out var capacity, out _);
            try
            {
                if (capacity < nv12.Length) throw new InvalidDataException("Media Foundation returned an undersized input buffer.");
                Marshal.Copy(nv12, 0, destination, nv12.Length);
                buffer.CurrentLength = nv12.Length;
            }
            finally
            {
                buffer.Unlock();
            }
            sample.AddBuffer(buffer);
        }
        var duration = 10_000_000L / _frameRate;
        sample.SampleTime = _nextSampleTime;
        sample.SampleDuration = duration;
        _nextSampleTime += duration;
        _transform.ProcessInput(0, sample, 0);
        _needInput = false;

        // An asynchronous MFT may immediately produce output or request the
        // next input first. Never manufacture a duplicate frame to satisfy it;
        // return an empty list and allow the bounded capture loop to submit the
        // next real frame.
        while (true)
        {
            using var mediaEvent = _events.GetEvent(0);
            mediaEvent.Status.CheckError();
            if (mediaEvent.EventType == MediaEventTypes.TransformHaveOutput)
            {
                TryReadOutput(outputs);
                return outputs;
            }
            if (mediaEvent.EventType == MediaEventTypes.TransformNeedInput)
            {
                _needInput = true;
                return outputs;
            }
            if (mediaEvent.EventType == MediaEventTypes.Error)
                throw new InvalidOperationException("The Windows H.264 hardware encoder reported a fatal event.");
        }
    }

    private void WaitUntilInputIsNeeded(List<H264EncodedFrame> outputs)
    {
        while (!_needInput)
        {
            using var mediaEvent = _events.GetEvent(0);
            mediaEvent.Status.CheckError();
            if (mediaEvent.EventType == MediaEventTypes.TransformNeedInput)
            {
                _needInput = true;
                break;
            }
            if (mediaEvent.EventType == MediaEventTypes.TransformHaveOutput)
            {
                TryReadOutput(outputs);
            }
            else if (mediaEvent.EventType == MediaEventTypes.Error)
            {
                throw new InvalidOperationException("The Windows H.264 hardware encoder reported a fatal event.");
            }
        }
    }

    private void TryReadOutput(List<H264EncodedFrame> outputs)
    {
        var info = _transform.GetOutputStreamInfo(0);
        IMFSample? suppliedSample = null;
        IMFMediaBuffer? suppliedBuffer = null;
        var output = new OutputDataBuffer { StreamID = 0 };
        try
        {
            if ((info.Flags & (OutputStreamProvidesSamples | OutputStreamCanProvideSamples)) == 0)
            {
                suppliedSample = MediaFactory.MFCreateSample();
                suppliedBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                suppliedSample.AddBuffer(suppliedBuffer);
                output.Sample = suppliedSample;
            }
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            if (result.Code == unchecked((int)0xC00D6D61))
            {
                // Hardware MFTs commonly announce their final sequence-header
                // format after the first input sample. Accept only the first
                // encoder-advertised H.264 output type, then retry the pending
                // output without dropping the captured frame.
                using var changedType = _transform.GetOutputAvailableType(0, 0);
                if (changedType.MajorType != MediaTypeGuids.Video
                    || changedType.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.H264)
                {
                    throw new InvalidDataException("The hardware encoder attempted an unsafe output format change.");
                }
                _transform.SetOutputType(0, changedType, 0);
                // The format-change result consumes this output notification.
                // The asynchronous MFT will queue a fresh HaveOutput event once
                // it is ready under the newly negotiated type.
                return;
            }
            if (result.Failure)
            {
                // MF_E_TRANSFORM_NEED_MORE_INPUT is expected when an output
                // event races a flush/format transition; all other failures
                // remain actionable.
                if (result.Code == unchecked((int)0xC00D6D72)) return;
                result.CheckError();
            }
            var encodedSample = output.Sample ?? suppliedSample;
            if (encodedSample is null || encodedSample.TotalLength <= 0) return;
            using var contiguous = encodedSample.ConvertToContiguousBuffer();
            var length = contiguous.CurrentLength;
            if (length is <= 0 or > 8 * 1024 * 1024)
                throw new InvalidDataException("The H.264 encoder returned an unsafe output size.");
            var bytes = new byte[length];
            contiguous.Lock(out var pointer, out _, out var currentLength);
            try
            {
                if (currentLength < length) throw new InvalidDataException("The H.264 output buffer changed during readback.");
                Marshal.Copy(pointer, bytes, 0, length);
            }
            finally
            {
                contiguous.Unlock();
            }
            outputs.Add(new H264EncodedFrame(
                bytes,
                encodedSample.SampleTime / 10,
                ContainsIdrNal(bytes)));
        }
        finally
        {
            output.Events?.Dispose();
            if (output.Sample is not null && !ReferenceEquals(output.Sample, suppliedSample)) output.Sample.Dispose();
            suppliedBuffer?.Dispose();
            suppliedSample?.Dispose();
        }
    }

    private static IMFTransform ActivateHardwareEncoder()
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            MftEnumFlagHardware | MftEnumFlagSortAndFilter,
            input,
            output,
            out var pointers,
            out var count);
        try
        {
            Exception? lastFailure = null;
            for (var index = 0u; index < count; index++)
            {
                var pointer = Marshal.ReadIntPtr(pointers, checked((int)(index * (uint)IntPtr.Size)));
                using var activation = new IMFActivate(pointer);
                try
                {
                    return activation.ActivateObject<IMFTransform>();
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                }
            }
            throw new NotSupportedException("Windows exposes no usable H.264 hardware encoder.", lastFailure);
        }
        finally
        {
            if (pointers != IntPtr.Zero) Marshal.FreeCoTaskMem(pointers);
        }
    }

    internal static byte[] ConvertBgraToNv12(byte[] bgra, int width, int height, int stride)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 dimensions must be even.");
        var yPlaneSize = checked(width * height);
        var result = new byte[checked(yPlaneSize + yPlaneSize / 2)];
        Parallel.For(0, height, y =>
        {
            var sourceRow = y * stride;
            var targetRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var offset = sourceRow + x * 4;
                var blue = bgra[offset];
                var green = bgra[offset + 1];
                var red = bgra[offset + 2];
                result[targetRow + x] = ClampByte(((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);
            }
        });
        Parallel.For(0, height / 2, uvY =>
        {
            var y = uvY * 2;
            var uvRow = yPlaneSize + (y / 2) * width;
            for (var x = 0; x < width; x += 2)
            {
                var red = 0;
                var green = 0;
                var blue = 0;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                {
                    var offset = (y + dy) * stride + (x + dx) * 4;
                    blue += bgra[offset];
                    green += bgra[offset + 1];
                    red += bgra[offset + 2];
                }
                red >>= 2;
                green >>= 2;
                blue >>= 2;
                result[uvRow + x] = ClampByte(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);
                result[uvRow + x + 1] = ClampByte(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);
            }
        });
        return result;
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static bool ContainsIdrNal(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index + 4 < bytes.Length; index++)
        {
            var start = bytes[index] == 0 && bytes[index + 1] == 0
                && (bytes[index + 2] == 1 || (bytes[index + 2] == 0 && bytes[index + 3] == 1));
            if (!start) continue;
            var header = index + (bytes[index + 2] == 1 ? 3 : 4);
            if (header < bytes.Length && (bytes[header] & 0x1F) == 5) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        try { _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
        _events.Dispose();
        _transform.Dispose();
        try { MediaFactory.MFShutdown().CheckError(); } catch { }
    }
}
