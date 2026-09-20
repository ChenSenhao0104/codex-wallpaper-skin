using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

/// <summary>
/// Raw Media Foundation interop for the v0.4 GPU encoder.
///
/// Every GUID and vtable index in this file is taken from the Windows SDK
/// headers (<c>mfidl.h</c>, <c>mfreadwrite.h</c>, <c>mfobjects.h</c>,
/// <c>mfapi.h</c>) rather than from memory, because a wrong attribute GUID fails
/// silently in Media Foundation. Methods that are called only a few times are
/// resolved by vtable index, matching the existing D3D11 usage in
/// <see cref="WindowsGraphicsCaptureSource"/>; interfaces that this process
/// implements (and therefore must expose a complete vtable for) are declared in
/// full.
/// </summary>
internal static class MediaFoundationInterop
{
    // mfapi.h: MF_VERSION = MF_SDK_VERSION << 16 | MF_API_VERSION (0x0002 << 16 | 0x0070).
    internal const uint MfVersion = 0x00020070;
    internal const uint MfStartupNoSocket = 0x1;

    // MFBYTESTREAM_* capability flags (mfobjects.h).
    internal const uint ByteStreamIsReadable = 0x1;
    internal const uint ByteStreamIsWritable = 0x2;
    internal const uint ByteStreamIsSeekable = 0x4;

    // MFBYTESTREAM_SEEK_ORIGIN (mfobjects.h).
    internal const int SeekOriginBegin = 0;
    internal const int SeekOriginCurrent = 1;

    // MFVideoInterlace_Mode (mfidl.h).
    internal const uint VideoInterlaceProgressive = 2;

    // eAVEncH264VProfile (codecapi.h).
    internal const uint H264ProfileMain = 77;
    internal const uint H264ProfileHigh = 100;

    internal const int S_OK = 0;
    internal const int E_FAIL = unchecked((int)0x80004005);
    internal const int E_INVALIDARG = unchecked((int)0x80070057);
    internal const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int E_POINTER = unchecked((int)0x80004003);
    internal const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    /// <summary>MF_MT_MAJOR_TYPE (mfidl.h).</summary>
    internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");

    /// <summary>MF_MT_SUBTYPE (mfidl.h).</summary>
    internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

    /// <summary>MF_MT_AVG_BITRATE (mfidl.h).</summary>
    internal static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");

    /// <summary>MF_MT_INTERLACE_MODE (mfidl.h).</summary>
    internal static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

    /// <summary>MF_MT_FRAME_SIZE (mfidl.h); packed as (width &lt;&lt; 32) | height.</summary>
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");

    /// <summary>MF_MT_FRAME_RATE (mfidl.h); packed as (numerator &lt;&lt; 32) | denominator.</summary>
    internal static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");

    /// <summary>MF_MT_PIXEL_ASPECT_RATIO (mfidl.h); packed as (numerator &lt;&lt; 32) | denominator.</summary>
    internal static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

    /// <summary>MF_MT_MPEG2_PROFILE (mfidl.h).</summary>
    internal static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    /// <summary>MF_MT_MAX_KEYFRAME_SPACING (mfidl.h).</summary>
    internal static readonly Guid MaximumKeyframeSpacing = new("c16eb52b-73a1-476f-8d62-839d6a020652");

    /// <summary>MF_MT_ALL_SAMPLES_INDEPENDENT (mfidl.h).</summary>
    internal static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");

    /// <summary>MF_MT_DEFAULT_STRIDE (mfidl.h). Used to declare the RGB row order to the encoder.</summary>
    internal static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    /// <summary>MF_LOW_LATENCY (mfidl.h).</summary>
    internal static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// <summary>MF_TRANSCODE_CONTAINERTYPE (mfidl.h).</summary>
    internal static readonly Guid TranscodeContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");

    /// <summary>MFTranscodeContainerType_FMPEG4 (mfidl.h). Fragmented MP4, which Media Source Extensions accepts.</summary>
    internal static readonly Guid ContainerTypeFragmentMpeg4 = new("9ba876f1-419f-4b77-a1e0-35959d9d4004");

    /// <summary>MF_SINK_WRITER_DISABLE_THROTTLING (mfreadwrite.h).</summary>
    internal static readonly Guid SinkWriterDisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    /// <summary>MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS (mfreadwrite.h).</summary>
    internal static readonly Guid ReadWriteEnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");

    /// <summary>MF_MPEG4SINK_MOOV_BEFORE_MDAT (mfidl.h). Places the init segment first so a live reader never seeks.</summary>
    internal static readonly Guid Mpeg4SinkMoovBeforeMdat = new("f672e3ac-e1e6-4f10-b5ec-5f3b30828816");

    /// <summary>
    /// MF_MPEG4SINK_MIN_FRAGMENT_DURATION (mfidl.h). The MPEG-4 sink starts a new
    /// moof once this much media time has accumulated, so it is the main control
    /// over how long the transport holds a frame before it can be presented.
    /// </summary>
    internal static readonly Guid Mpeg4SinkMinFragmentDuration = new("a30b570c-8efd-45e8-94fe-27c84b5bdff6");

    /// <summary>MF_TRANSFORM_ASYNC (mftransform.h). Hardware encoder MFTs are asynchronous; the software MFT is not.</summary>
    internal static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");

    /// <summary>IID_IMFTransform (mftransform.h).</summary>
    internal static readonly Guid TransformInterfaceId = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    /// <summary>MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING (mfreadwrite.h). Inserts the video processor for RGB conversion.</summary>
    internal static readonly Guid SourceReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    // IMFSourceReader vtable indices (mfreadwrite.h). GetStreamSelection(3)
    // SetStreamSelection(4) GetNativeMediaType(5) GetCurrentMediaType(6)
    // SetCurrentMediaType(7) SetCurrentPosition(8) ReadSample(9) Flush(10)
    // GetServiceForStream(11) GetPresentationAttribute(12).
    private const int SourceReaderSetCurrentMediaType = 7;
    private const int SourceReaderReadSample = 9;
    private const int SampleConvertToContiguousBuffer = 41;

    internal static int SetSourceReaderMediaType(IntPtr reader, int streamIndex, IntPtr mediaType) =>
        Method<SetCurrentMediaTypeDelegate>(reader, SourceReaderSetCurrentMediaType)(
            reader, streamIndex, IntPtr.Zero, mediaType);

    internal static int ReadSample(
        IntPtr reader,
        int streamIndex,
        uint controlFlags,
        out int actualStreamIndex,
        out int streamFlags,
        out long timestamp,
        out IntPtr sample) =>
        Method<ReadSampleDelegate>(reader, SourceReaderReadSample)(
            reader, streamIndex, controlFlags, out actualStreamIndex, out streamFlags, out timestamp, out sample);

    internal static int ConvertSampleToBuffer(IntPtr sample, out IntPtr buffer) =>
        Method<ConvertToContiguousBufferDelegate>(sample, SampleConvertToContiguousBuffer)(sample, out buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentMediaTypeDelegate(IntPtr self, int streamIndex, IntPtr reserved, IntPtr mediaType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadSampleDelegate(
        IntPtr self,
        int streamIndex,
        uint controlFlags,
        out int actualStreamIndex,
        out int streamFlags,
        out long timestamp,
        out IntPtr sample);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConvertToContiguousBufferDelegate(IntPtr self, out IntPtr buffer);

    /// <summary>MFMediaType_Video (mfapi.h).</summary>
    internal static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");

    /// <summary>MFVideoFormat_RGB32 (mfapi.h, D3DFMT_X8R8G8B8 = 22). Byte order in memory is B, G, R, X.</summary>
    internal static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");

    /// <summary>MFVideoFormat_ARGB32 (mfapi.h, D3DFMT_A8R8G8B8 = 21).</summary>
    internal static readonly Guid VideoFormatArgb32 = new("00000015-0000-0010-8000-00aa00389b71");

    /// <summary>MFVideoFormat_NV12 (mfapi.h, FCC 'NV12').</summary>
    internal static readonly Guid VideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    /// <summary>MFVideoFormat_H264 (mfapi.h, FCC 'H264').</summary>
    internal static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");

    // IMFAttributes vtable indices (mfobjects.h method order, IUnknown = 0..2).
    private const int AttributeGetUInt32 = 7;
    private const int AttributeSetUInt32 = 21;
    private const int AttributeSetUInt64 = 22;
    private const int AttributeSetGuid = 24;

    // IMFTransform vtable indices. GetStreamLimits(3) GetStreamCount(4)
    // GetStreamIDs(5) GetInputStreamInfo(6) GetOutputStreamInfo(7) GetAttributes(8).
    private const int TransformGetAttributes = 8;

    // IMFSample vtable indices. IMFAttributes occupies 3..32, then:
    // GetSampleFlags(33) SetSampleFlags(34) GetSampleTime(35) SetSampleTime(36)
    // GetSampleDuration(37) SetSampleDuration(38) GetBufferCount(39)
    // GetBufferByIndex(40) ConvertToContiguousBuffer(41) AddBuffer(42).
    private const int SampleSetSampleTime = 36;
    private const int SampleSetSampleDuration = 38;
    private const int SampleAddBuffer = 42;

    // IMFMediaBuffer vtable indices: Lock(3) Unlock(4) GetCurrentLength(5) SetCurrentLength(6).
    private const int BufferLock = 3;
    private const int BufferUnlock = 4;
    private const int BufferSetCurrentLength = 6;

    internal static bool Succeeded(int hr) => hr >= 0;

    internal static Exception Failure(int hr, string operation)
    {
        var message = $"{operation} failed with HRESULT 0x{hr:X8}.";
        var exception = Marshal.GetExceptionForHR(hr);
        return exception is null ? new InvalidOperationException(message) : new InvalidOperationException(message, exception);
    }

    internal static void ThrowIfFailed(int hr, string operation)
    {
        if (!Succeeded(hr))
        {
            throw Failure(hr, operation);
        }
    }

    /// <summary>Starts Media Foundation once per process. Called before any MF object is created.</summary>
    internal static bool TryStartup(out string failure)
    {
        var hr = MFStartup(MfVersion, MfStartupNoSocket);
        if (Succeeded(hr))
        {
            failure = string.Empty;
            return true;
        }
        failure = $"Media Foundation platform startup failed (0x{hr:X8}).";
        return false;
    }

    internal static void Shutdown() => MFShutdown();

    internal static MfSinkWriter? CreateSinkWriter(
        IntPtr byteStream,
        TimeSpan minimumFragmentDuration,
        bool preferHardware,
        bool lowLatency,
        bool hintFragmentDuration,
        out IntPtr attributes,
        out string failure)
    {
        var store = IntPtr.Zero;
        var hr = MFCreateAttributes(out store, 8);
        if (!Succeeded(hr))
        {
            attributes = IntPtr.Zero;
            failure = $"MFCreateAttributes failed (0x{hr:X8}).";
            return null;
        }

        // Hardware transforms first: the whole point of the v0.4 path is that the
        // H.264 encode runs on the GPU. When no hardware encoder can be created
        // Media Foundation silently uses the software encoder instead, which the
        // encoder reports through DetectEncoderMode rather than assuming success.
        //
        // MF_SINK_WRITER_DISABLE_THROTTLING is deliberately absent: the default
        // throttle is the pipeline's backpressure signal, so a slow consumer
        // slows capture instead of growing an unbounded queue.
        var requested = new List<(Guid Key, uint Value, string Name)>
        {
            (TranscodeContainerType, 0, nameof(TranscodeContainerType))
        };
        if (preferHardware)
        {
            requested.Add((ReadWriteEnableHardwareTransforms, 1, nameof(ReadWriteEnableHardwareTransforms)));
        }
        requested.Add((Mpeg4SinkMoovBeforeMdat, 1, nameof(Mpeg4SinkMoovBeforeMdat)));
        if (lowLatency)
        {
            requested.Add((LowLatency, 1, nameof(LowLatency)));
        }
        if (hintFragmentDuration)
        {
            requested.Add((Mpeg4SinkMinFragmentDuration,
                (uint)Math.Clamp(minimumFragmentDuration.TotalMilliseconds, 50, 5000),
                nameof(Mpeg4SinkMinFragmentDuration)));
        }

        var rejected = new List<string>();
        foreach (var (key, value, name) in requested)
        {
            if (key == TranscodeContainerType)
            {
                continue;
            }
            var attributeResult = SetAttributeUInt32(store, key, value);
            if (!Succeeded(attributeResult))
            {
                rejected.Add($"{name} (0x{attributeResult:X8})");
            }
        }

        var container = SetAttributeGuid(store, TranscodeContainerType, ContainerTypeFragmentMpeg4);
        if (!Succeeded(container))
        {
            failure = $"The fragmented MP4 container type was rejected (0x{container:X8}).";
            Marshal.Release(store);
            attributes = IntPtr.Zero;
            return null;
        }

        var writerHr = MFCreateSinkWriterFromURL(null, byteStream, store, out var writer);
        if (!Succeeded(writerHr) || writer == IntPtr.Zero)
        {
            failure = $"MFCreateSinkWriterFromURL failed (0x{writerHr:X8}). "
                + $"Rejected optional attributes: {(rejected.Count == 0 ? "none" : string.Join(", ", rejected))}.";
            Marshal.Release(store);
            attributes = IntPtr.Zero;
            return null;
        }

        attributes = store;
        failure = string.Empty;
        return new MfSinkWriter(writer);
    }

    /// <summary>Reads IMFTransform::GetAttributes for an encoder MFT returned by IMFSinkWriter::GetServiceForStream.</summary>
    internal static IntPtr TryGetTransformAttributes(IntPtr transform)
    {
        var hr = GetMethod<GetAttributesDelegate>(transform, TransformGetAttributes)(transform, out var attributes);
        return Succeeded(hr) ? attributes : IntPtr.Zero;
    }

    internal static bool TryGetAttributeUInt32(IntPtr attributes, Guid key, out uint value)
    {
        var hr = GetMethod<GetUInt32Delegate>(attributes, AttributeGetUInt32)(attributes, ref key, out value);
        return Succeeded(hr);
    }

    internal static int SetAttributeUInt32(IntPtr attributes, Guid key, uint value) =>
        GetMethod<SetUInt32Delegate>(attributes, AttributeSetUInt32)(attributes, ref key, value);

    internal static int SetAttributeUInt64(IntPtr attributes, Guid key, ulong value) =>
        GetMethod<SetUInt64Delegate>(attributes, AttributeSetUInt64)(attributes, ref key, value);

    internal static int SetAttributeGuid(IntPtr attributes, Guid key, Guid value) =>
        GetMethod<SetGuidDelegate>(attributes, AttributeSetGuid)(attributes, ref key, ref value);

    internal static int SetSampleTime(IntPtr sample, long time) =>
        GetMethod<SetSampleTimeDelegate>(sample, SampleSetSampleTime)(sample, time);

    internal static int SetSampleDuration(IntPtr sample, long duration) =>
        GetMethod<SetSampleDurationDelegate>(sample, SampleSetSampleDuration)(sample, duration);

    internal static int AddSampleBuffer(IntPtr sample, IntPtr buffer) =>
        GetMethod<AddBufferDelegate>(sample, SampleAddBuffer)(sample, buffer);

    internal static int LockBuffer(IntPtr buffer, out IntPtr data, out uint maximumLength, out uint currentLength) =>
        GetMethod<LockDelegate>(buffer, BufferLock)(buffer, out data, out maximumLength, out currentLength);

    internal static int UnlockBuffer(IntPtr buffer) =>
        GetMethod<UnlockDelegate>(buffer, BufferUnlock)(buffer);

    internal static int SetBufferLength(IntPtr buffer, uint length) =>
        GetMethod<SetCurrentLengthDelegate>(buffer, BufferSetCurrentLength)(buffer, length);

    /// <summary>
    /// Completes the asynchronous byte-stream callback contract, shared by the
    /// write and read paths.
    ///
    /// IMFByteStream::BeginWrite and BeginRead must invoke their
    /// IMFAsyncCallback before the caller may call EndWrite or EndRead. A stream
    /// that performs the operation immediately but never signals completion
    /// makes the MPEG-4 sink wait forever, which surfaces as WriteSample never
    /// returning rather than as an error.
    /// </summary>
    internal static void CompleteAsyncCallback(IntPtr callback, IntPtr state, int status)
    {
        IntPtr result = IntPtr.Zero;
        try
        {
            var hr = MFCreateAsyncResult(IntPtr.Zero, callback, state, out result);
            if (!Succeeded(hr) || result == IntPtr.Zero)
            {
                return;
            }
            Method<SetAsyncResultStatusDelegate>(result, AsyncResultSetStatus)(result, status);
            Method<InvokeAsyncCallbackDelegate>(callback, AsyncCallbackInvoke)(callback, result);
        }
        catch
        {
            // A failure here is reported through the byte stream's own failure
            // state; it must not escape into Media Foundation's callback thread.
        }
        finally
        {
            if (result != IntPtr.Zero)
            {
                Marshal.Release(result);
            }
        }
    }

    private static T GetMethod<T>(IntPtr instance, int index) where T : Delegate => Method<T>(instance, index);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int AddStreamDelegate(IntPtr self, IntPtr targetMediaType, out int streamIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SetInputMediaTypeDelegate(
        IntPtr self, int streamIndex, IntPtr inputMediaType, IntPtr encodingParameters);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int BeginWritingDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int WriteSampleDelegate(IntPtr self, int streamIndex, IntPtr sample);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int FinalizeDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetServiceForStreamDelegate(
        IntPtr self, int streamIndex, ref Guid service, ref Guid interfaceId, out IntPtr value);

    // IMFAsyncCallback vtable: GetParameters(3) Invoke(4).
    private const int AsyncCallbackInvoke = 4;

    // IMFAsyncResult vtable: GetState(3) GetStatus(4) SetStatus(5) GetObject(6) GetStateNoAddRef(7).
    private const int AsyncResultSetStatus = 5;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InvokeAsyncCallbackDelegate(IntPtr self, IntPtr asyncResult);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetAsyncResultStatusDelegate(IntPtr self, int status);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUInt32Delegate(IntPtr self, ref Guid key, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt32Delegate(IntPtr self, ref Guid key, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAttributesDelegate(IntPtr self, out IntPtr attributes);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUInt64Delegate(IntPtr self, ref Guid key, ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetGuidDelegate(IntPtr self, ref Guid key, ref Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSampleTimeDelegate(IntPtr self, long time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSampleDurationDelegate(IntPtr self, long duration);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddBufferDelegate(IntPtr self, IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockDelegate(IntPtr self, out IntPtr data, out uint maximumLength, out uint currentLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentLengthDelegate(IntPtr self, uint length);

    internal const string SinkWriterGuid = "3137f1cd-fe5e-4805-a5d8-fb477448cb3d";

    // IMFSinkWriter vtable indices (mfreadwrite.h). AddStream(3) SetInputMediaType(4)
    // BeginWriting(5) WriteSample(6) SendStreamTick(7) PlaceMarker(8)
    // NotifyEndOfSegment(9) Flush(10) Finalize(11) GetServiceForStream(12)
    // GetStatistics(13).
    internal const int SinkWriterAddStream = 3;
    internal const int SinkWriterSetInputMediaType = 4;
    internal const int SinkWriterBeginWriting = 5;
    internal const int SinkWriterWriteSample = 6;
    internal const int SinkWriterFinalize = 11;
    internal const int SinkWriterGetServiceForStream = 12;

    internal static T Method<T>(IntPtr instance, int index) where T : Delegate
    {
        if (instance == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(MediaFoundationInterop));
        }
        var vtable = Marshal.ReadIntPtr(instance);
        var function = Marshal.ReadIntPtr(vtable, checked(index * IntPtr.Size));
        if (function == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Media Foundation object has no method at vtable index {index}.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMemoryBuffer(uint maximumLength, out IntPtr buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAsyncResult(
        IntPtr objectPointer, IntPtr callback, IntPtr state, out IntPtr asyncResult);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int MFCreateSourceReaderFromURL(
        string url, IntPtr attributes, out IntPtr sourceReader);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    internal static extern int MFCreateSourceReaderFromByteStream(
        IntPtr byteStream, IntPtr attributes, out IntPtr sourceReader);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSinkWriterFromURL(
        string? outputUrl,
        IntPtr byteStream,
        IntPtr attributes,
        out IntPtr sinkWriter);
}

/// <summary>
/// IMFSinkWriter (mfreadwrite.h, IID 3137f1cd-fe5e-4805-a5d8-fb477448cb3d)
/// called through its vtable.
///
/// Media Foundation objects are free-threaded but are not registered with a
/// proxy/stub, so a managed COM callable wrapper (RCW) only works from the
/// apartment that created it: a call from a thread-pool continuation fails with
/// E_NOINTERFACE. Direct vtable invocation performs no marshalling and is
/// apartment agnostic, which is why the existing D3D11 capture code uses the
/// same technique.
/// </summary>
internal sealed class MfSinkWriter : IDisposable
{
    private readonly IntPtr _instance;
    private bool _disposed;

    internal MfSinkWriter(IntPtr instance) => _instance = instance;

    internal IntPtr Instance => _instance;

    public int AddStream(IntPtr targetMediaType, out int streamIndex) =>
        MediaFoundationInterop.Method<MediaFoundationInterop.AddStreamDelegate>(
            _instance, MediaFoundationInterop.SinkWriterAddStream)(_instance, targetMediaType, out streamIndex);

    public int SetInputMediaType(int streamIndex, IntPtr inputMediaType, IntPtr encodingParameters) =>
        MediaFoundationInterop.Method<MediaFoundationInterop.SetInputMediaTypeDelegate>(
            _instance, MediaFoundationInterop.SinkWriterSetInputMediaType)(
            _instance, streamIndex, inputMediaType, encodingParameters);

    public int BeginWriting() =>
        MediaFoundationInterop.Method<MediaFoundationInterop.BeginWritingDelegate>(
            _instance, MediaFoundationInterop.SinkWriterBeginWriting)(_instance);

    public int WriteSample(int streamIndex, IntPtr sample) =>
        MediaFoundationInterop.Method<MediaFoundationInterop.WriteSampleDelegate>(
            _instance, MediaFoundationInterop.SinkWriterWriteSample)(_instance, streamIndex, sample);

    public int FinalizeWriter() =>
        MediaFoundationInterop.Method<MediaFoundationInterop.FinalizeDelegate>(
            _instance, MediaFoundationInterop.SinkWriterFinalize)(_instance);

    public int GetServiceForStream(int streamIndex, ref Guid service, ref Guid interfaceId, out IntPtr value) =>
        MediaFoundationInterop.Method<MediaFoundationInterop.GetServiceForStreamDelegate>(
            _instance, MediaFoundationInterop.SinkWriterGetServiceForStream)(
            _instance, streamIndex, ref service, ref interfaceId, out value);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_instance != IntPtr.Zero)
        {
            Marshal.Release(_instance);
        }
    }
}

/// <summary>
/// IMFByteStream (mfobjects.h, IID ad4c1b00-4bf7-422f-9175-756693d9130d).
/// Implemented in managed code and handed to Media Foundation as a COM callable
/// wrapper, so the fragment bytes never touch the file system.
/// </summary>
[ComImport]
[Guid("ad4c1b00-4bf7-422f-9175-756693d9130d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFByteStream
{
    [PreserveSig]
    int GetCapabilities(out uint capabilities);

    [PreserveSig]
    int GetLength(out ulong length);

    [PreserveSig]
    int SetLength(ulong length);

    [PreserveSig]
    int GetCurrentPosition(out ulong position);

    [PreserveSig]
    int SetCurrentPosition(ulong position);

    [PreserveSig]
    int IsEndOfStream(out int endOfStream);

    [PreserveSig]
    int Read(IntPtr buffer, uint count, out uint read);

    [PreserveSig]
    int BeginRead(IntPtr buffer, uint count, IntPtr callback, IntPtr state);

    [PreserveSig]
    int EndRead(IntPtr result, out uint read);

    [PreserveSig]
    int Write(IntPtr buffer, uint count, out uint written);

    [PreserveSig]
    int BeginWrite(IntPtr buffer, uint count, IntPtr callback, IntPtr state);

    [PreserveSig]
    int EndWrite(IntPtr result, out uint written);

    [PreserveSig]
    int Seek(int seekOrigin, long seekOffset, uint seekFlags, out ulong position);

    [PreserveSig]
    int Flush();

    [PreserveSig]
    int Close();
}
