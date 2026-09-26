using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace CodexWallpaperSkin;

internal sealed record H264EncodedFrame(
    byte[] Data,
    long TimestampMicroseconds,
    bool KeyFrame);

/// <summary>
/// Low-latency H.264 elementary-stream encoder backed by a Windows hardware
/// Media Foundation transform. The preferred path converts the captured D3D11
/// BGRA texture to an NV12 DXGI surface on the GPU. A bounded system-memory
/// NV12 path remains available for incompatible drivers.
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
    private readonly byte[]? _nv12Buffer;
    private readonly GpuInputPipeline? _gpuInput;
    private bool _needInput;
    private long _nextSampleTime;
    private bool _disposed;

    private MediaFoundationH264Encoder(
        IMFTransform transform,
        IMFMediaEventGenerator events,
        int width,
        int height,
        int frameRate,
        int outputBufferSize,
        long startSampleTime,
        GpuInputPipeline? gpuInput)
    {
        _transform = transform;
        _events = events;
        _width = width;
        _height = height;
        _frameRate = frameRate;
        _outputBufferSize = outputBufferSize;
        _gpuInput = gpuInput;
        _nv12Buffer = gpuInput is null ? new byte[checked(width * height * 3 / 2)] : null;
        _nextSampleTime = Math.Max(0, startSampleTime);
    }

    public long NextSampleTime100Nanoseconds => _nextSampleTime;

    public static MediaFoundationH264Encoder Create(
        int width,
        int height,
        int frameRate,
        int bitrate,
        long startSampleTime = 0)
    {
        return CreateCore(width, height, frameRate, bitrate, startSampleTime, null, null);
    }

    public static MediaFoundationH264Encoder CreateGpu(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int width,
        int height,
        int frameRate,
        int bitrate,
        long startSampleTime = 0)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);
        return CreateCore(width, height, frameRate, bitrate, startSampleTime, device, context);
    }

    private static MediaFoundationH264Encoder CreateCore(
        int width,
        int height,
        int frameRate,
        int bitrate,
        long startSampleTime,
        ID3D11Device? device,
        ID3D11DeviceContext? context)
    {
        width &= ~1;
        height &= ~1;
        if (width is < 64 or > 4096 || height is < 64 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(width));
        frameRate = Math.Clamp(frameRate, 15, 60);
        bitrate = Math.Clamp(bitrate, 1_000_000, 60_000_000);

        MediaFactory.MFStartup().CheckError();
        IMFTransform? transform = null;
        IMFMediaEventGenerator? events = null;
        GpuInputPipeline? gpuInput = null;
        try
        {
            transform = ActivateHardwareEncoder();
            transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true).CheckError();
            if (device is not null && context is not null)
            {
                gpuInput = GpuInputPipeline.Create(device, context, width, height, frameRate);
                transform.ProcessMessage(
                    TMessageType.MessageSetD3DManager,
                    unchecked((nuint)gpuInput.DeviceManager.NativePointer.ToInt64()));
            }

            using (var outputType = MediaFactory.MFCreateMediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, checked((uint)bitrate)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(frameRate, 1)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1)).CheckError();
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
                SetBt709ColorMetadata(outputType);
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
                SetBt709ColorMetadata(inputType);
                transform.SetInputType(0, inputType, 0);
            }

            events = transform.QueryInterface<IMFMediaEventGenerator>();
            var outputInfo = transform.GetOutputStreamInfo(0);
            var outputBufferSize = Math.Max(outputInfo.Size, 4 * 1024 * 1024);
            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            return new MediaFoundationH264Encoder(
                transform, events, width, height, frameRate, outputBufferSize, startSampleTime, gpuInput);
        }
        catch
        {
            gpuInput?.Dispose();
            if (gpuInput is null)
            {
                context?.Dispose();
                device?.Dispose();
            }
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
        var nv12Buffer = _nv12Buffer
            ?? throw new InvalidOperationException("This encoder accepts D3D11 textures, not CPU frames.");
        ConvertBgraToNv12(bgra, _width, _height, sourceStride, nv12Buffer);
        using var sample = MediaFactory.MFCreateSample();
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(nv12Buffer.Length))
        {
            buffer.Lock(out var destination, out var capacity, out _);
            try
            {
                if (capacity < nv12Buffer.Length) throw new InvalidDataException("Media Foundation returned an undersized input buffer.");
                Marshal.Copy(nv12Buffer, 0, destination, nv12Buffer.Length);
                buffer.CurrentLength = nv12Buffer.Length;
            }
            finally
            {
                buffer.Unlock();
            }
            sample.AddBuffer(buffer);
        }
        SubmitInputSample(sample);

        return WaitForOutputOrNextInput(outputs);
    }

    public IReadOnlyList<H264EncodedFrame> EncodeTexture(
        ID3D11Texture2D texture,
        int sourceWidth,
        int sourceHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(texture);
        if (sourceWidth < _width || sourceHeight < _height)
            throw new ArgumentException("The source texture is smaller than the configured encoder surface.", nameof(texture));
        var gpuInput = _gpuInput
            ?? throw new InvalidOperationException("This encoder accepts CPU frames, not D3D11 textures.");

        var outputs = new List<H264EncodedFrame>(2);
        WaitUntilInputIsNeeded(outputs);
        var nv12Texture = gpuInput.Convert(texture);
        using var sample = MediaFactory.MFCreateSample();
        using (var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
            typeof(ID3D11Texture2D).GUID,
            nv12Texture,
            0,
            false))
        {
            sample.AddBuffer(buffer);
        }
        SubmitInputSample(sample);
        return WaitForOutputOrNextInput(outputs);
    }

    private void SubmitInputSample(IMFSample sample)
    {
        var duration = 10_000_000L / _frameRate;
        sample.SampleTime = _nextSampleTime;
        sample.SampleDuration = duration;
        _nextSampleTime += duration;
        _transform.ProcessInput(0, sample, 0);
        _needInput = false;
    }

    private IReadOnlyList<H264EncodedFrame> WaitForOutputOrNextInput(List<H264EncodedFrame> outputs)
    {

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

    private sealed class GpuInputPipeline : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11VideoDevice _videoDevice;
        private readonly ID3D11VideoContext _videoContext;
        private readonly ID3D11VideoProcessorEnumerator _enumerator;
        private readonly ID3D11VideoProcessor _processor;
        private readonly ID3D11Texture2D[] _targets;
        private readonly ID3D11VideoProcessorOutputView[] _outputViews;
        private int _targetIndex;

        private GpuInputPipeline(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11VideoDevice videoDevice,
            ID3D11VideoContext videoContext,
            ID3D11VideoProcessorEnumerator enumerator,
            ID3D11VideoProcessor processor,
            ID3D11Texture2D[] targets,
            ID3D11VideoProcessorOutputView[] outputViews,
            IMFDXGIDeviceManager deviceManager)
        {
            _device = device;
            _context = context;
            _videoDevice = videoDevice;
            _videoContext = videoContext;
            _enumerator = enumerator;
            _processor = processor;
            _targets = targets;
            _outputViews = outputViews;
            DeviceManager = deviceManager;
        }

        public IMFDXGIDeviceManager DeviceManager { get; }

        public static GpuInputPipeline Create(
            ID3D11Device device,
            ID3D11DeviceContext context,
            int width,
            int height,
            int frameRate)
        {
            ID3D11VideoDevice? videoDevice = null;
            ID3D11VideoContext? videoContext = null;
            ID3D11VideoProcessorEnumerator? enumerator = null;
            ID3D11VideoProcessor? processor = null;
            IMFDXGIDeviceManager? manager = null;
            var targets = new List<ID3D11Texture2D>(2);
            var outputViews = new List<ID3D11VideoProcessorOutputView>(2);
            try
            {
                videoDevice = device.QueryInterface<ID3D11VideoDevice>();
                videoContext = context.QueryInterface<ID3D11VideoContext>();
                var content = new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive,
                    InputFrameRate = new Rational((uint)frameRate, 1),
                    InputWidth = (uint)width,
                    InputHeight = (uint)height,
                    OutputFrameRate = new Rational((uint)frameRate, 1),
                    OutputWidth = (uint)width,
                    OutputHeight = (uint)height,
                    Usage = VideoUsage.OptimalSpeed
                };
                enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
                var bgraSupport = enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm);
                var nv12Support = enumerator.CheckVideoProcessorFormat(Format.NV12);
                if ((bgraSupport & VideoProcessorFormatSupport.Input) == 0
                    || (nv12Support & VideoProcessorFormatSupport.Output) == 0)
                {
                    throw new NotSupportedException("The active GPU cannot convert captured BGRA textures to encoder-ready NV12 surfaces.");
                }
                processor = videoDevice.CreateVideoProcessor(enumerator, 0);
                videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
                videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);

                var targetDescription = new Texture2DDescription(
                    Format.NV12,
                    (uint)width,
                    (uint)height,
                    1,
                    1,
                    BindFlags.RenderTarget,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    1,
                    0,
                    ResourceOptionFlags.None);
                var outputDescription = new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
                };
                for (var index = 0; index < 2; index++)
                {
                    var target = device.CreateTexture2D(in targetDescription);
                    targets.Add(target);
                    outputViews.Add(videoDevice.CreateVideoProcessorOutputView(target, enumerator, outputDescription));
                }

                manager = MediaFactory.MFCreateDXGIDeviceManager();
                manager.ResetDevice(device).CheckError();
                return new GpuInputPipeline(
                    device, context, videoDevice, videoContext, enumerator, processor,
                    targets.ToArray(), outputViews.ToArray(), manager);
            }
            catch
            {
                manager?.Dispose();
                foreach (var view in outputViews) view.Dispose();
                foreach (var target in targets) target.Dispose();
                processor?.Dispose();
                enumerator?.Dispose();
                videoContext?.Dispose();
                videoDevice?.Dispose();
                throw;
            }
        }

        public ID3D11Texture2D Convert(ID3D11Texture2D source)
        {
            var inputDescription = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
            };
            using var inputView = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, inputDescription);
            var index = _targetIndex++ & 1;
            var stream = new VideoProcessorStream
            {
                Enable = true,
                InputSurface = inputView
            };
            _videoContext.VideoProcessorBlt(_processor, _outputViews[index], 0, 1, [stream]);
            return _targets[index];
        }

        public void Dispose()
        {
            DeviceManager.Dispose();
            foreach (var view in _outputViews) view.Dispose();
            foreach (var target in _targets) target.Dispose();
            _processor.Dispose();
            _enumerator.Dispose();
            _videoContext.Dispose();
            _videoDevice.Dispose();
            _context.Dispose();
            _device.Dispose();
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
        ConvertBgraToNv12(bgra, width, height, stride, result);
        return result;
    }

    private static void ConvertBgraToNv12(
        byte[] bgra,
        int width,
        int height,
        int stride,
        byte[] result)
    {
        var yPlaneSize = checked(width * height);
        if (result.Length < checked(yPlaneSize + yPlaneSize / 2))
            throw new ArgumentException("The reusable NV12 buffer is too small.", nameof(result));
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
                result[targetRow + x] = ClampByte(((47 * red + 157 * green + 16 * blue + 128) >> 8) + 16);
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
                result[uvRow + x] = ClampByte(((-26 * red - 87 * green + 113 * blue + 128) >> 8) + 128);
                result[uvRow + x + 1] = ClampByte(((112 * red - 102 * green - 10 * blue + 128) >> 8) + 128);
            }
        });
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static void SetBt709ColorMetadata(IMFMediaType mediaType)
    {
        mediaType.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709).CheckError();
        mediaType.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709).CheckError();
        mediaType.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)VideoTransferMatrix.Bt709).CheckError();
        mediaType.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)NominalRange.Range16_235).CheckError();
    }

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
        _gpuInput?.Dispose();
        try { MediaFactory.MFShutdown().CheckError(); } catch { }
    }
}
