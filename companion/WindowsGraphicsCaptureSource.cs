using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace CodexWallpaperSkin;

/// <summary>
/// Captures an HWND through Windows Graphics Capture.
///
/// Two frame shapes are supported because the product has two media paths. The
/// compatibility path (default) publishes a frozen <see cref="BitmapSource"/> so
/// frames can be JPEG encoded for the reduced-frame-rate CDP transport. The GPU
/// path publishes the raw top-down BGRA bytes that the Media Foundation hardware
/// encoder consumes directly, with no image encode and no BitmapSource
/// allocation per frame.
///
/// Both frame channels have capacity two and drop the oldest frame, so a slow
/// consumer can never build latency or memory by replaying obsolete animation.
/// </summary>
internal sealed class WindowsGraphicsCaptureSource : IAsyncDisposable
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;
    private const int D3D11UsageStaging = 3;
    private const int D3D11MapRead = 1;
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiDeviceGuid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid D3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly object _gate = new();
    private readonly Channel<BitmapSource> _frames = Channel.CreateBounded<BitmapSource>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    private readonly Channel<byte[]>? _rawFrames;
    private readonly bool _rawPixels;
    private readonly bool _countingOnly;
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _captureSession;
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private IntPtr _stagingTexture;
    private D3D11Texture2DDesc _stagingDescription;
    private int _frameWidth;
    private int _frameHeight;
    private long _publishedRawFrames;
    private long _consumedRawFrames;
    private bool _disposed;

    /// <summary>Raw frames produced by the capture callback since the source started.</summary>
    public long PublishedRawFrames => Interlocked.Read(ref _publishedRawFrames);

    /// <summary>Raw frames taken by the consumer. The difference is what the bounded channel superseded.</summary>
    public long ConsumedRawFrames => Interlocked.Read(ref _consumedRawFrames);

    /// <summary>
    /// Frames the raw channel can hold. The difference between published and
    /// consumed frames includes these still queued, so only frames beyond the
    /// capacity were actually superseded and dropped.
    /// </summary>
    public const int RawFrameCapacity = 2;

    private WindowsGraphicsCaptureSource(
        GraphicsCaptureItem item,
        IDirect3DDevice winRtDevice,
        IntPtr d3dDevice,
        IntPtr d3dContext,
        bool rawPixels,
        bool countingOnly = false)
    {
        _item = item;
        _winRtDevice = winRtDevice;
        _d3dDevice = d3dDevice;
        _d3dContext = d3dContext;
        _rawPixels = rawPixels;
        _countingOnly = countingOnly;
        if (rawPixels)
        {
            _rawFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(RawFrameCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
        }
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _item.Size);
        _captureSession = _framePool.CreateCaptureSession(_item);
        try { _captureSession.IsCursorCaptureEnabled = false; } catch { }
        _framePool.FrameArrived += FramePool_FrameArrived;
        _item.Closed += Item_Closed;
        _captureSession.StartCapture();
    }

    /// <summary>
    /// True when this source publishes raw BGRA pixels instead of bitmaps.
    /// </summary>
    public bool UsesRawPixels => _rawPixels;

    /// <summary>
    /// Counts frames without copying or mapping anything.
    ///
    /// This exists to answer one question with evidence rather than assumption:
    /// is a low frame rate caused by the capture consumer's GPU-to-CPU readback
    /// stalling the shared GPU, or by the source itself rendering slowly? With
    /// counting enabled the callback performs no copy, no map and no allocation,
    /// so the measured rate is the source's own ceiling.
    /// </summary>
    public bool UsesCountingOnly => _countingOnly;

    /// <summary>Size of the most recent frame, or (0, 0) before the first frame arrives.</summary>
    public (int Width, int Height) FrameSize
    {
        get
        {
            lock (_gate)
            {
                return (_frameWidth, _frameHeight);
            }
        }
    }

    public static WindowsGraphicsCaptureSource? TryStart(IntPtr window) => TryStart(window, rawPixels: false, out _);

    public static WindowsGraphicsCaptureSource? TryStartRaw(IntPtr window, out string failure) =>
        TryStart(window, rawPixels: true, out failure);

    public static WindowsGraphicsCaptureSource? TryStartCountOnly(IntPtr window, out string failure) =>
        TryStart(window, rawPixels: false, out failure, countingOnly: true);

    private static WindowsGraphicsCaptureSource? TryStart(
        IntPtr window,
        bool rawPixels,
        out string failure,
        bool countingOnly = false)
    {
        failure = string.Empty;
        if (window == IntPtr.Zero || !GraphicsCaptureSession.IsSupported())
        {
            failure = "Windows Graphics Capture is unavailable on this system.";
            return null;
        }
        IntPtr d3dDevice = IntPtr.Zero, d3dContext = IntPtr.Zero;
        try
        {
            var item = CreateItemForWindow(window);
            if (item.Size.Width < 64 || item.Size.Height < 64)
            {
                failure = "The Wallpaper Engine render surface is too small to capture.";
                return null;
            }
            CreateD3DDevice(out d3dDevice, out d3dContext, out var winRtDevice);
            return new WindowsGraphicsCaptureSource(item, winRtDevice, d3dDevice, d3dContext, rawPixels, countingOnly);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            if (d3dContext != IntPtr.Zero) Marshal.Release(d3dContext);
            if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
            return null;
        }
    }

    public ValueTask<BitmapSource> ReadFrameAsync(CancellationToken cancellationToken) =>
        _frames.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Blocks for the newest raw BGRA frame. Returns false on timeout or when the
    /// capture closed, which the caller treats as a bounded capture interruption
    /// rather than a fatal error.
    /// </summary>
    public bool TryReadRawFrame(int timeoutMilliseconds, out byte[]? pixels, out int width, out int height)
    {
        pixels = null;
        lock (_gate)
        {
            width = _frameWidth;
            height = _frameHeight;
        }
        var channel = _rawFrames;
        if (channel is null)
        {
            return false;
        }
        if (channel.Reader.TryRead(out pixels))
        {
            Interlocked.Increment(ref _consumedRawFrames);
            lock (_gate)
            {
                width = _frameWidth;
                height = _frameHeight;
            }
            return true;
        }
        if (!channel.Reader.WaitToReadAsync().AsTask().Wait(Math.Max(1, timeoutMilliseconds)))
        {
            return false;
        }
        if (!channel.Reader.TryRead(out pixels))
        {
            return false;
        }
        Interlocked.Increment(ref _consumedRawFrames);
        lock (_gate)
        {
            width = _frameWidth;
            height = _frameHeight;
        }
        return true;
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                if (_countingOnly)
                {
                    // No copy, no map, no allocation: this is the source's own rate.
                    Interlocked.Increment(ref _publishedRawFrames);
                    return;
                }
                if (_rawPixels)
                {
                    var pixels = CopySurfacePixels(frame.Surface, out var width, out var height);
                    _frameWidth = width;
                    _frameHeight = height;
                    Interlocked.Increment(ref _publishedRawFrames);
                    _rawFrames?.Writer.TryWrite(pixels);
                    return;
                }
                var bitmap = CopySurface(frame.Surface);
                _frames.Writer.TryWrite(bitmap);
            }
            catch (Exception exception)
            {
                _frames.Writer.TryComplete(exception);
                _rawFrames?.Writer.TryComplete(exception);
            }
        }
    }

    private void Item_Closed(GraphicsCaptureItem sender, object args)
    {
        var exception = new IOException("The Wallpaper Engine capture window was closed.");
        _frames.Writer.TryComplete(exception);
        _rawFrames?.Writer.TryComplete(exception);
    }

    private BitmapSource CopySurface(IDirect3DSurface surface)
    {
        var pixels = CopySurfacePixels(surface, out var width, out var height);
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Copies the newest D3D11 surface into a tightly packed, top-down BGRA
    /// buffer. Both media paths share this code so the compatibility backend and
    /// the GPU backend can never disagree about geometry or orientation.
    /// </summary>
    private byte[] CopySurfacePixels(IDirect3DSurface surface, out int width, out int height)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var textureGuid = D3D11Texture2DGuid;
        ThrowIfFailed(access.GetInterface(ref textureGuid, out var sourceTexture));
        try
        {
            var getDescription = GetComMethod<GetTexture2DDescriptionDelegate>(sourceTexture, 10);
            getDescription(sourceTexture, out var description);
            if (description.Width < 64 || description.Height < 64
                || description.Width > 4096 || description.Height > 4096
                || (long)description.Width * description.Height > 10_000_000)
            {
                throw new InvalidDataException("Windows Graphics Capture returned an unsafe frame size.");
            }
            EnsureStagingTexture(description);
            var copyResource = GetComMethod<CopyResourceDelegate>(_d3dContext, 47);
            copyResource(_d3dContext, _stagingTexture, sourceTexture);

            var map = GetComMethod<MapDelegate>(_d3dContext, 14);
            ThrowIfFailed(map(_d3dContext, _stagingTexture, 0, D3D11MapRead, 0, out var mapped));
            try
            {
                width = checked((int)description.Width);
                height = checked((int)description.Height);
                var stride = checked(width * 4);
                if (mapped.Data == IntPtr.Zero || mapped.RowPitch < stride)
                {
                    throw new InvalidDataException("Windows Graphics Capture returned an invalid mapped surface.");
                }
                var pixels = new byte[checked(stride * height)];
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(IntPtr.Add(mapped.Data, checked((int)(row * mapped.RowPitch))), pixels, row * stride, stride);
                }
                return pixels;
            }
            finally
            {
                var unmap = GetComMethod<UnmapDelegate>(_d3dContext, 15);
                unmap(_d3dContext, _stagingTexture, 0);
            }
        }
        finally
        {
            Marshal.Release(sourceTexture);
        }
    }

    private void EnsureStagingTexture(D3D11Texture2DDesc source)
    {
        if (_stagingTexture != IntPtr.Zero
            && _stagingDescription.Width == source.Width
            && _stagingDescription.Height == source.Height
            && _stagingDescription.Format == source.Format)
        {
            return;
        }
        if (_stagingTexture != IntPtr.Zero)
        {
            Marshal.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }
        var staging = source;
        staging.MipLevels = 1;
        staging.ArraySize = 1;
        staging.SampleDescription = new DxgiSampleDescription { Count = 1, Quality = 0 };
        staging.Usage = D3D11UsageStaging;
        staging.BindFlags = 0;
        staging.CpuAccessFlags = D3D11CpuAccessRead;
        staging.MiscFlags = 0;
        var createTexture = GetComMethod<CreateTexture2DDelegate>(_d3dDevice, 5);
        ThrowIfFailed(createTexture(_d3dDevice, ref staging, IntPtr.Zero, out _stagingTexture));
        _stagingDescription = staging;
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        IntPtr className = IntPtr.Zero, factoryPointer = IntPtr.Zero, itemPointer = IntPtr.Zero;
        object? factoryObject = null;
        try
        {
            ThrowIfFailed(WindowsCreateString(
                "Windows.Graphics.Capture.GraphicsCaptureItem",
                "Windows.Graphics.Capture.GraphicsCaptureItem".Length,
                out className));
            var interopGuid = GraphicsCaptureItemInteropGuid;
            ThrowIfFailed(RoGetActivationFactory(className, ref interopGuid, out factoryPointer));
            factoryObject = Marshal.GetObjectForIUnknown(factoryPointer);
            var interop = (IGraphicsCaptureItemInterop)factoryObject;
            var itemGuid = GraphicsCaptureItemGuid;
            ThrowIfFailed(interop.CreateForWindow(window, ref itemGuid, out itemPointer));
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            if (itemPointer != IntPtr.Zero) Marshal.Release(itemPointer);
            if (factoryObject is not null && Marshal.IsComObject(factoryObject)) Marshal.FinalReleaseComObject(factoryObject);
            if (factoryPointer != IntPtr.Zero) Marshal.Release(factoryPointer);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
        }
    }

    private static void CreateD3DDevice(
        out IntPtr d3dDevice,
        out IntPtr d3dContext,
        out IDirect3DDevice winRtDevice)
    {
        var result = D3D11CreateDevice(
            IntPtr.Zero, D3DDriverTypeHardware, IntPtr.Zero, D3D11CreateDeviceBgraSupport,
            IntPtr.Zero, 0, D3D11SdkVersion, out d3dDevice, out _, out d3dContext);
        if (result < 0)
        {
            result = D3D11CreateDevice(
                IntPtr.Zero, D3DDriverTypeWarp, IntPtr.Zero, D3D11CreateDeviceBgraSupport,
                IntPtr.Zero, 0, D3D11SdkVersion, out d3dDevice, out _, out d3dContext);
        }
        ThrowIfFailed(result);

        IntPtr dxgiDevice = IntPtr.Zero, inspectable = IntPtr.Zero;
        try
        {
            var dxgiGuid = DxgiDeviceGuid;
            ThrowIfFailed(Marshal.QueryInterface(d3dDevice, ref dxgiGuid, out dxgiDevice));
            ThrowIfFailed(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable));
            winRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        catch
        {
            if (d3dContext != IntPtr.Zero) Marshal.Release(d3dContext);
            if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
            d3dContext = IntPtr.Zero;
            d3dDevice = IntPtr.Zero;
            throw;
        }
        finally
        {
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
            if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
        }
    }

    private static T GetComMethod<T>(IntPtr instance, int index) where T : Delegate
    {
        if (instance == IntPtr.Zero) throw new ObjectDisposedException(nameof(WindowsGraphicsCaptureSource));
        var vtable = Marshal.ReadIntPtr(instance);
        var function = Marshal.ReadIntPtr(vtable, checked(index * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _frames.Writer.TryComplete();
            _rawFrames?.Writer.TryComplete();
            try { _framePool.FrameArrived -= FramePool_FrameArrived; } catch { }
            try { _item.Closed -= Item_Closed; } catch { }
            try { _captureSession.Dispose(); } catch { }
            try { _framePool.Dispose(); } catch { }
            if (_stagingTexture != IntPtr.Zero) Marshal.Release(_stagingTexture);
            if (_d3dContext != IntPtr.Zero) Marshal.Release(_d3dContext);
            if (_d3dDevice != IntPtr.Zero) Marshal.Release(_d3dDevice);
            _stagingTexture = IntPtr.Zero;
            _d3dContext = IntPtr.Zero;
            _d3dDevice = IntPtr.Zero;
        }
        return ValueTask.CompletedTask;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(ref Guid iid, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSampleDescription
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public DxgiSampleDescription SampleDescription;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(
        IntPtr self, ref D3D11Texture2DDesc description, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTexture2DDescriptionDelegate(IntPtr self, out D3D11Texture2DDesc description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(
        IntPtr self, IntPtr resource, uint subresource, int mapType, uint mapFlags,
        out D3D11MappedSubresource mappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(IntPtr self, IntPtr resource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(IntPtr self, IntPtr destination, IntPtr source);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr value);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr className, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
