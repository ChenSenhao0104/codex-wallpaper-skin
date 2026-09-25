using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CodexWallpaperSkin;

public sealed record CapturedPointer(
    double X,
    double Y,
    int Buttons,
    int WheelDelta,
    bool Hidden,
    bool Inside);

public sealed record NativeStreamMetrics(
    long CapturedFrames,
    long EncoderInputs,
    long EncodedFrames,
    double CaptureMilliseconds,
    double EncodeMilliseconds,
    double ElapsedSeconds,
    int CaptureWidth = 0,
    int CaptureHeight = 0,
    int TargetFrameRate = 0,
    int TargetBitrate = 0);

/// <summary>
/// Uses Wallpaper Engine itself as the renderer for Scene projects and large
/// Wallpaper Engine videos. Frames are
/// captured from a private off-screen play-in-window surface, while pointer
/// coordinates received from the Codex page are forwarded to that surface.
/// </summary>
public sealed class WallpaperEngineCaptureSession : IAsyncDisposable
{
    private const uint PrintWindowRenderFullContent = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExNoActivate = 0x08000000L;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint WmClose = 0x0010;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmMiddleButtonDown = 0x0207;
    private const uint WmMiddleButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmMouseLeave = 0x02A3;
    private const uint CwpSkipDisabled = 0x0002;
    private const uint CwpSkipTransparent = 0x0004;
    private const nuint MkLeftButton = 0x0001;
    private const nuint MkRightButton = 0x0002;
    private const nuint MkMiddleButton = 0x0010;
    private const int MaximumFrameBytes = 2 * 1024 * 1024;
    private const int MaximumConsecutiveStreamFailures = 8;
    private readonly string _engineExecutable;
    private readonly string _windowName;
    private readonly IntPtr _windowHandle;
    private readonly CancellationTokenSource _lifetime = new();
    private WindowsGraphicsCaptureSource? _graphicsCapture;
    private Task? _streamTask;
    private int _frameRate;
    private bool _pauseWhenHidden;
    private volatile bool _lastPageHidden;
    private byte[] _lastEncodedFrame;
    private readonly double _baseRate;
    private readonly double _baseVolume;
    private int _lastPointerButtons;
    private int _stopRequested;
    private bool _disposed;
    private readonly int? _configuredEngineFrameRateLimit;
    private long _capturedFrames;
    private long _encoderInputs;
    private long _encodedFrames;
    private long _captureTicks;
    private long _encodeTicks;
    private long _streamStarted;
    private int _activeEncoderBitrate;

    private WallpaperEngineCaptureSession(
        string engineExecutable,
        string windowName,
        IntPtr windowHandle,
        WindowsGraphicsCaptureSource? graphicsCapture,
        byte[] initialFrame,
        int frameRate,
        bool pauseWhenHidden,
        double baseRate,
        double baseVolume,
        int? configuredEngineFrameRateLimit)
    {
        _engineExecutable = engineExecutable;
        _windowName = windowName;
        _windowHandle = windowHandle;
        _graphicsCapture = graphicsCapture;
        InitialFrame = initialFrame;
        _lastEncodedFrame = initialFrame;
        _frameRate = NormalizeFrameRate(frameRate);
        _pauseWhenHidden = pauseWhenHidden;
        _baseRate = baseRate;
        _baseVolume = baseVolume;
        _configuredEngineFrameRateLimit = configuredEngineFrameRateLimit;
    }

    public byte[] InitialFrame { get; }
    public bool UsesWindowsGraphicsCapture => _graphicsCapture is not null;
    public bool CanUseHardwareH264 => _graphicsCapture is not null;
    public int CaptureWidth => _graphicsCapture?.CaptureWidth ?? 0;
    public int CaptureHeight => _graphicsCapture?.CaptureHeight ?? 0;
    public int? ConfiguredWallpaperEngineFrameRateLimit => _configuredEngineFrameRateLimit;
    public bool IsRunning => !_disposed && _streamTask is { IsCompleted: false };
    public Task Completion => _streamTask ?? Task.CompletedTask;
    public NativeStreamMetrics StreamMetrics
    {
        get
        {
            var started = Volatile.Read(ref _streamStarted);
            var elapsed = started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started);
            return new NativeStreamMetrics(
                Interlocked.Read(ref _capturedFrames),
                Interlocked.Read(ref _encoderInputs),
                Interlocked.Read(ref _encodedFrames),
                StopwatchTicksToMilliseconds(Interlocked.Read(ref _captureTicks)),
                StopwatchTicksToMilliseconds(Interlocked.Read(ref _encodeTicks)),
                elapsed.TotalSeconds,
                CaptureWidth,
                CaptureHeight,
                Volatile.Read(ref _frameRate),
                Volatile.Read(ref _activeEncoderBitrate));
        }
    }

    public static bool CanUse(WallpaperEntry wallpaper) =>
        wallpaper.UsesWallpaperEngineCapture
        && !string.IsNullOrWhiteSpace(wallpaper.ProjectPath)
        && TryResolveEngine(wallpaper.ProjectPath, out _, out _);

    internal static int? GetConfiguredFrameRateLimit(WallpaperEntry wallpaper)
    {
        if (string.IsNullOrWhiteSpace(wallpaper.ProjectPath)
            || !TryResolveEngine(wallpaper.ProjectPath, out var engineRoot, out _))
        {
            return null;
        }
        return ReadConfiguredFrameRateLimit(engineRoot);
    }

    public static async Task<WallpaperEngineCaptureSession> StartAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        int viewportWidth,
        int viewportHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        ArgumentNullException.ThrowIfNull(settings);
        if (!wallpaper.UsesWallpaperEngineCapture || string.IsNullOrWhiteSpace(wallpaper.ProjectPath))
        {
            throw new InvalidDataException("Wallpaper Engine capture requires a contained Scene or native-video project.");
        }
        if (!TryResolveEngine(wallpaper.ProjectPath, out var engineRoot, out var executable))
        {
            throw new FileNotFoundException("Wallpaper Engine wallpaper64.exe was not found beside this Workshop project.");
        }

        var projectPath = Path.GetFullPath(wallpaper.ProjectPath);
        var projectInfo = new FileInfo(projectPath);
        if (!projectInfo.Exists || (projectInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("The Wallpaper Engine project.json is unavailable or unsafe.", projectPath);
        }
        if (wallpaper.Kind == WallpaperKind.Video)
        {
            WallpaperCatalog.ValidateNativeWallpaperEngineVideo(wallpaper);
        }

        var (width, height) = CalculateCaptureSize(
            viewportWidth,
            viewportHeight,
            settings.SceneResolutionScale);
        var windowName = "Codex Wallpaper Skin " + Guid.NewGuid().ToString("N");
        var controlExecutable = await EnsureEngineRunningAsync(engineRoot, executable, cancellationToken);
        await RunControlWithStartupRetryAsync(controlExecutable,
            ["-control", "openWallpaper", "-file", projectPath, "-playInWindow", windowName,
             "-width", width.ToString(), "-height", height.ToString(),
             "-x", "-32000", "-y", "-32000", "-borderless"], cancellationToken);

        var handle = await WaitForWindowAsync(windowName, engineRoot, cancellationToken);
        try
        {
            ConfigurePrivateRenderWindow(handle);
            var properties = WallpaperEnginePropertyReader.Read(wallpaper, settings);
            var appliedRate = ReadNumber(properties, "rate", 100);
            var baseRate = appliedRate / Math.Max(.01, settings.PlaybackRate);
            var baseVolume = ReadNumber(properties, "volume", 100);
            if (settings.Muted) properties["volume"] = 0;
            if (properties.Count > 0)
            {
                var json = JsonSerializer.Serialize(properties);
                await RunPropertyControlWithRetryAsync(controlExecutable, json, windowName, cancellationToken);
            }

            var graphicsCapture = WindowsGraphicsCaptureSource.TryStart(handle);
            byte[] initialFrame;
            if (graphicsCapture is not null)
            {
                try
                {
                    initialFrame = await CaptureFirstGoodFrameAsync(graphicsCapture, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await graphicsCapture.DisposeAsync();
                    throw;
                }
                catch
                {
                    await graphicsCapture.DisposeAsync();
                    graphicsCapture = null;
                    initialFrame = await CaptureFirstGoodFrameAsync(handle, cancellationToken);
                }
            }
            else
            {
                initialFrame = await CaptureFirstGoodFrameAsync(handle, cancellationToken);
            }
            return new WallpaperEngineCaptureSession(
                controlExecutable, windowName, handle, graphicsCapture, initialFrame, settings.SceneFrameRate,
                settings.PauseWhenHidden, baseRate, baseVolume, ReadConfiguredFrameRateLimit(engineRoot));
        }
        catch
        {
            await TryCloseWindowAsync(controlExecutable, windowName, handle);
            throw;
        }
    }

    public void StartStreaming(
        Func<byte[], CancellationToken, Task> publishFrame,
        Func<CancellationToken, Task<CapturedPointer?>> readPointer)
    {
        ArgumentNullException.ThrowIfNull(publishFrame);
        ArgumentNullException.ThrowIfNull(readPointer);
        if (_disposed) throw new ObjectDisposedException(nameof(WallpaperEngineCaptureSession));
        if (_streamTask is not null) throw new InvalidOperationException("Wallpaper Engine capture is already streaming.");
        Volatile.Write(ref _streamStarted, Stopwatch.GetTimestamp());
        _streamTask = Task.WhenAll(
            Task.Run(() => StreamFramesAsync(publishFrame, _lifetime.Token)),
            Task.Run(() => StreamPointerAsync(readPointer, _lifetime.Token)));
    }

    internal void StartH264Streaming(
        Func<H264EncodedFrame, CancellationToken, Task> publishFrame)
    {
        ArgumentNullException.ThrowIfNull(publishFrame);
        if (_disposed) throw new ObjectDisposedException(nameof(WallpaperEngineCaptureSession));
        if (_graphicsCapture is null) throw new NotSupportedException("Windows Graphics Capture is required for H.264 streaming.");
        if (_streamTask is not null) throw new InvalidOperationException("Wallpaper Engine capture is already streaming.");
        // v0.4 deliberately drops Wallpaper Engine mouse-effect emulation.
        // Avoiding the old 30 Hz pointer CDP polling also leaves the control
        // channel available for coalesced video batches and removes a source of
        // stream cancellation when two CDP requests overlap.
        Volatile.Write(ref _streamStarted, Stopwatch.GetTimestamp());
        _streamTask = Task.Run(() => StreamH264FramesAsync(publishFrame, _lifetime.Token));
    }

    public async Task UpdateSettingsAsync(WallpaperSettings settings, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        _frameRate = NormalizeFrameRate(settings.SceneFrameRate);
        _pauseWhenHidden = settings.PauseWhenHidden;
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["rate"] = (int)Math.Round(Math.Clamp(_baseRate * settings.PlaybackRate, 10, 200)),
            ["volume"] = settings.Muted ? 0 : (int)Math.Round(Math.Clamp(_baseVolume, 0, 100))
        };
        var json = JsonSerializer.Serialize(properties);
        await RunPropertyControlWithRetryAsync(_engineExecutable, json, _windowName, cancellationToken);
    }

    private async Task StreamFramesAsync(
        Func<byte[], CancellationToken, Task> publishFrame,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0
                && !cancellationToken.IsCancellationRequested
                && IsWindow(_windowHandle))
            {
                if (_pauseWhenHidden && _lastPageHidden)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken);
                    continue;
                }
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var frame = await CaptureNextFrameAsync(cancellationToken);
                    await publishFrame(frame, cancellationToken);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaximumConsecutiveStreamFailures)
                    {
                        Volatile.Write(ref _stopRequested, 1);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 75 * consecutiveFailures)), cancellationToken);
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                var interval = TimeSpan.FromSeconds(1d / Math.Max(1, _frameRate));
                if (elapsed < interval)
                {
                    await Task.Delay(interval - elapsed, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task StreamH264FramesAsync(
        Func<H264EncodedFrame, CancellationToken, Task> publishFrame,
        CancellationToken cancellationToken)
    {
        var graphicsCapture = _graphicsCapture
            ?? throw new NotSupportedException("Windows Graphics Capture is required for H.264 streaming.");
        MediaFoundationH264Encoder? encoder = null;
        var encoderWidth = 0;
        var encoderHeight = 0;
        var encoderFrameRate = 0;
        var nextSampleTime = 0L;
        long? firstCaptureTimestamp = null;
        var consecutiveFailures = 0;
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0
                && !cancellationToken.IsCancellationRequested
                && IsWindow(_windowHandle))
            {
                if (_pauseWhenHidden && _lastPageHidden)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    continue;
                }
                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var captureStarted = Stopwatch.GetTimestamp();
                    var frame = await graphicsCapture.ReadFrameAsync(timeout.Token);
                    Interlocked.Add(ref _captureTicks, Stopwatch.GetTimestamp() - captureStarted);
                    Interlocked.Increment(ref _capturedFrames);
                    if (!CapturedFrameQuality.IsAcceptable(frame))
                    {
                        // Hold the browser's last decoded frame instead of
                        // encoding a transient black/white capture surface.
                        continue;
                    }
                    var width = frame.Width & ~1;
                    var height = frame.Height & ~1;
                    var frameRate = NormalizeFrameRate(Volatile.Read(ref _frameRate));
                    if (encoder is null
                        || encoderWidth != width
                        || encoderHeight != height
                        || encoderFrameRate != frameRate)
                    {
                        encoder?.Dispose();
                        var bitrate = CalculateH264Bitrate(width, height, frameRate);
                        encoder = MediaFoundationH264Encoder.Create(
                            width, height, frameRate, bitrate, nextSampleTime);
                        encoderWidth = width;
                        encoderHeight = height;
                        encoderFrameRate = frameRate;
                        Volatile.Write(ref _activeEncoderBitrate, bitrate);
                    }
                    var encodeStarted = Stopwatch.GetTimestamp();
                    if (frame.CapturedAtTimestamp > 0)
                    {
                        firstCaptureTimestamp ??= frame.CapturedAtTimestamp;
                    }
                    var capturedSampleTime = firstCaptureTimestamp is long origin
                        ? StopwatchTicksToHundredNanoseconds(
                            Math.Max(0, frame.CapturedAtTimestamp - origin))
                        : (long?)null;
                    var outputs = encoder.EncodeBgra(
                        frame.Pixels, frame.Width, frame.Height, frame.Stride, capturedSampleTime);
                    nextSampleTime = encoder.NextSampleTime100Nanoseconds;
                    Interlocked.Add(ref _encodeTicks, Stopwatch.GetTimestamp() - encodeStarted);
                    Interlocked.Increment(ref _encoderInputs);
                    foreach (var encoded in outputs)
                    {
                        Interlocked.Increment(ref _encodedFrames);
                        await publishFrame(encoded, cancellationToken);
                    }
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaximumConsecutiveStreamFailures)
                    {
                        Volatile.Write(ref _stopRequested, 1);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(750, 50 * consecutiveFailures)), cancellationToken);
                }
                var elapsed = Stopwatch.GetElapsedTime(started);
                var interval = TimeSpan.FromSeconds(1d / Math.Max(1, Volatile.Read(ref _frameRate)));
                if (elapsed < interval) await Task.Delay(interval - elapsed, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    internal static int CalculateH264Bitrate(int width, int height, int frameRate)
    {
        // Favor clarity over network-style compression: this is a local-only
        // stream. The higher ceiling reduces gradients and fine-line smearing
        // while keeping batches bounded and within hardware encoder limits.
        var estimated = (long)width * height * frameRate / 3;
        return (int)Math.Clamp(estimated, 8_000_000, 60_000_000);
    }

    internal static (int Width, int Height) CalculateCaptureSize(
        int viewportWidth,
        int viewportHeight,
        double requestedScale)
    {
        const int maximumWidth = 2560;
        const int maximumHeight = 1600;
        var sourceWidth = Math.Clamp(viewportWidth, 640, 4096);
        var sourceHeight = Math.Clamp(viewportHeight, 400, 4096);
        var scale = Math.Clamp(requestedScale, 0.5, 1);
        var desiredWidth = sourceWidth * scale;
        var desiredHeight = sourceHeight * scale;
        var fit = Math.Min(1, Math.Min(maximumWidth / desiredWidth, maximumHeight / desiredHeight));
        var width = Math.Max(2, (int)Math.Round(desiredWidth * fit)) & ~1;
        var height = Math.Max(2, (int)Math.Round(desiredHeight * fit)) & ~1;
        return (width, height);
    }

    private static double StopwatchTicksToMilliseconds(long ticks) =>
        ticks <= 0 ? 0 : ticks * 1000d / Stopwatch.Frequency;

    internal static long StopwatchTicksToHundredNanoseconds(long ticks) =>
        ticks <= 0 ? 0 : checked((long)Math.Round(
            ticks * (10_000_000d / Stopwatch.Frequency),
            MidpointRounding.AwayFromZero));

    private async Task<byte[]> CaptureNextFrameAsync(CancellationToken cancellationToken)
    {
        var graphicsCapture = _graphicsCapture;
        if (graphicsCapture is null)
        {
            var compatibleFrame = CaptureJpeg(_windowHandle);
            _lastEncodedFrame = compatibleFrame;
            return compatibleFrame;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var frame = EncodeCapturedFrame(await graphicsCapture.ReadFrameAsync(timeout.Token));
            _lastEncodedFrame = frame;
            return frame;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // A Scene may intentionally stop producing new frames while it is
            // visually static. Keep the last verified WGC frame instead of
            // permanently downgrading the whole session to PrintWindow.
            return _lastEncodedFrame;
        }
        catch
        {
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref _graphicsCapture, null, graphicsCapture),
                graphicsCapture))
            {
                await graphicsCapture.DisposeAsync();
            }
            var compatibleFrame = CaptureJpeg(_windowHandle);
            _lastEncodedFrame = compatibleFrame;
            return compatibleFrame;
        }
    }

    private void ForwardPointer(CapturedPointer pointer)
    {
        if (!GetClientRect(_windowHandle, out var rect)) return;
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var x = Math.Clamp((int)Math.Round(pointer.X * (width - 1)), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(pointer.Y * (height - 1)), 0, height - 1);
        var target = FindPointerTarget(_windowHandle, x, y, out var targetPoint);
        var lParam = PackPoint(targetPoint.X, targetPoint.Y);
        var effectiveButtons = pointer.Hidden || !pointer.Inside ? 0 : pointer.Buttons;
        var keyState = PointerKeyState(effectiveButtons);
        if (pointer.Hidden || !pointer.Inside)
        {
            ForwardButton(target, lParam, effectiveButtons, 1, WmLeftButtonDown, WmLeftButtonUp, keyState);
            ForwardButton(target, lParam, effectiveButtons, 2, WmRightButtonDown, WmRightButtonUp, keyState);
            ForwardButton(target, lParam, effectiveButtons, 4, WmMiddleButtonDown, WmMiddleButtonUp, keyState);
            PostMessage(target, WmMouseLeave, 0, 0);
            _lastPointerButtons = 0;
            return;
        }
        PostMessage(target, WmMouseMove, keyState, lParam);
        ForwardButton(target, lParam, effectiveButtons, 1, WmLeftButtonDown, WmLeftButtonUp, keyState);
        ForwardButton(target, lParam, effectiveButtons, 2, WmRightButtonDown, WmRightButtonUp, keyState);
        ForwardButton(target, lParam, effectiveButtons, 4, WmMiddleButtonDown, WmMiddleButtonUp, keyState);
        if (pointer.WheelDelta != 0)
        {
            var screenPoint = new NativePoint { X = x, Y = y };
            ClientToScreen(_windowHandle, ref screenPoint);
            var wheelState = keyState | ((nuint)(ushort)(short)Math.Clamp(pointer.WheelDelta, -1200, 1200) << 16);
            PostMessage(target, WmMouseWheel, wheelState, PackPoint(screenPoint.X, screenPoint.Y));
        }
        _lastPointerButtons = effectiveButtons;
    }

    private void ForwardButton(
        IntPtr target,
        nint lParam,
        int buttons,
        int flag,
        uint downMessage,
        uint upMessage,
        nuint keyState)
    {
        var wasDown = (_lastPointerButtons & flag) != 0;
        var isDown = (buttons & flag) != 0;
        if (wasDown != isDown)
        {
            PostMessage(target, isDown ? downMessage : upMessage, keyState, lParam);
        }
    }

    private async Task StreamPointerAsync(
        Func<CancellationToken, Task<CapturedPointer?>> readPointer,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0
                && !cancellationToken.IsCancellationRequested
                && IsWindow(_windowHandle))
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var pointer = await readPointer(cancellationToken);
                    if (pointer is not null)
                    {
                        _lastPageHidden = pointer.Hidden;
                        ForwardPointer(pointer);
                    }
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaximumConsecutiveStreamFailures)
                    {
                        Volatile.Write(ref _stopRequested, 1);
                        break;
                    }
                }

                // Input is deliberately independent from capture. Slow or
                // rejected DirectX frames must not freeze an interactive Scene.
                var interval = consecutiveFailures == 0
                    ? TimeSpan.FromMilliseconds(1000d / 30)
                    : TimeSpan.FromMilliseconds(Math.Min(500, 50 * consecutiveFailures));
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed < interval)
                {
                    await Task.Delay(interval - elapsed, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static nuint PointerKeyState(int buttons)
    {
        nuint result = 0;
        if ((buttons & 1) != 0) result |= MkLeftButton;
        if ((buttons & 2) != 0) result |= MkRightButton;
        if ((buttons & 4) != 0) result |= MkMiddleButton;
        return result;
    }

    private static IntPtr FindPointerTarget(IntPtr root, int x, int y, out NativePoint targetPoint)
    {
        var target = root;
        targetPoint = new NativePoint { X = x, Y = y };
        for (var depth = 0; depth < 8; depth++)
        {
            var child = ChildWindowFromPointEx(target, targetPoint, CwpSkipDisabled | CwpSkipTransparent);
            if (child == IntPtr.Zero || child == target || !BelongsToSameProcess(root, child)) break;
            var screenPoint = targetPoint;
            if (!ClientToScreen(target, ref screenPoint) || !ScreenToClient(child, ref screenPoint)) break;
            target = child;
            targetPoint = screenPoint;
        }
        return target;
    }

    private static bool BelongsToSameProcess(IntPtr first, IntPtr second)
    {
        GetWindowThreadProcessId(first, out var firstProcess);
        GetWindowThreadProcessId(second, out var secondProcess);
        return firstProcess != 0 && firstProcess == secondProcess;
    }

    private static nint PackPoint(int x, int y) => (nint)(((ushort)y << 16) | (ushort)x);

    private static async Task<byte[]> CaptureFirstGoodFrameAsync(IntPtr handle, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await Task.Run(() => CaptureJpeg(handle), cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                lastFailure = exception;
                await Task.Delay(120, cancellationToken);
            }
        }
        throw new InvalidDataException("Wallpaper Engine did not produce a complete, usable frame within 8 seconds.", lastFailure);
    }

    private static async Task<byte[]> CaptureFirstGoodFrameAsync(
        WindowsGraphicsCaptureSource source,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Exception? lastFailure = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                return EncodeCapturedFrame(await source.ReadFrameAsync(timeout.Token));
            }
            catch (InvalidDataException exception)
            {
                lastFailure = exception;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidDataException(
            "Windows Graphics Capture did not produce a complete, usable frame within 5 seconds.",
            lastFailure);
    }

    private static byte[] CaptureJpeg(IntPtr handle)
    {
        if (!IsWindow(handle) || !GetClientRect(handle, out var rect))
        {
            throw new IOException("The Wallpaper Engine render window is no longer available.");
        }
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 64 || height < 64 || width > 4096 || height > 4096 || (long)width * height > 10_000_000)
        {
            throw new InvalidDataException("Wallpaper Engine returned an unsafe capture size.");
        }

        var windowDc = GetDC(handle);
        if (windowDc == IntPtr.Zero) throw new IOException("Windows could not access the Wallpaper Engine window surface.");
        var memoryDc = CreateCompatibleDC(windowDc);
        var bitmapHandle = memoryDc == IntPtr.Zero ? IntPtr.Zero : CreateCompatibleBitmap(windowDc, width, height);
        if (memoryDc == IntPtr.Zero || bitmapHandle == IntPtr.Zero)
        {
            if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(handle, windowDc);
            throw new IOException("Windows could not allocate the Wallpaper Engine capture surface.");
        }
        var previous = SelectObject(memoryDc, bitmapHandle);
        try
        {
            if (!PrintWindow(handle, memoryDc, PrintWindowRenderFullContent))
            {
                throw new IOException("Windows could not capture the Wallpaper Engine render window.");
            }
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return EncodeCapturedFrame(source);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(handle, windowDc);
        }
    }

    private static byte[] EncodeCapturedFrame(BitmapSource source)
    {
        if (!CapturedFrameQuality.IsAcceptable(source))
        {
            throw new InvalidDataException("Wallpaper Engine returned an empty or uniform transient frame.");
        }
        var encoded = EncodeJpeg(source, 85);
        if (encoded.Length > MaximumFrameBytes) encoded = EncodeJpeg(source, 65);
        if (encoded.Length is <= 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("The captured Wallpaper Engine frame exceeded the streaming budget.");
        }
        return encoded;
    }

    private static byte[] EncodeCapturedFrame(CapturedBgraFrame frame)
    {
        if (!CapturedFrameQuality.IsAcceptable(frame))
        {
            throw new InvalidDataException("Wallpaper Engine returned an empty or uniform transient frame.");
        }
        return EncodeCapturedFrame(frame.ToBitmapSource());
    }

    private static byte[] EncodeJpeg(BitmapSource bitmap, int quality)
    {
        using var stream = new MemoryStream();
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static double ReadNumber(IReadOnlyDictionary<string, object?> values, string key, double fallback)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        return double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static async Task<IntPtr> WaitForWindowAsync(
        string windowName,
        string engineRoot,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = FindWindow(null, windowName);
            if (handle != IntPtr.Zero && IsExpectedWallpaperEngineWindow(handle, engineRoot))
            {
                return handle;
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("Wallpaper Engine did not create its private render window within 15 seconds.");
    }

    private static bool IsExpectedWallpaperEngineWindow(IntPtr handle, string engineRoot)
    {
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return false;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var image = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(image)) return false;
            var normalizedRoot = Path.GetFullPath(engineRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedImage = Path.GetFullPath(image);
            return normalizedImage.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(normalizedImage).StartsWith("wallpaper", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wallpaper Engine creates play-in-window surfaces as ordinary top-level
    /// windows. Moving one off-screen does not keep it out of the taskbar or
    /// Alt+Tab. Convert it to a non-activating tool window before WGC starts,
    /// while leaving it shown so Windows continues to compose and capture it.
    /// </summary>
    private static void ConfigurePrivateRenderWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw new Win32Exception(
                "Wallpaper Engine closed its private render window before it could be configured.");
        }

        // If Explorer already observed the window, a brief hide/style/show
        // transition removes its existing taskbar button. The window is shown
        // again before WGC or PrintWindow is initialized, so capture is never
        // asked to consume a minimized, hidden, or cloaked surface.
        ShowWindow(handle, SwHide);
        var currentStyle = ReadExtendedWindowStyle(handle);
        var privateStyle = ToPrivateRenderExtendedStyle(currentStyle);
        if (privateStyle != currentStyle)
        {
            Marshal.SetLastPInvokeError(0);
            var previous = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(privateStyle));
            var error = Marshal.GetLastPInvokeError();
            if (previous == IntPtr.Zero && error != 0)
            {
                throw new Win32Exception(error,
                    "Windows could not exclude the Wallpaper Engine render window from task switching.");
            }
        }

        if (!SetWindowPos(
                handle, IntPtr.Zero, -32000, -32000, 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Windows could not move the private Wallpaper Engine render surface off-screen.");
        }
        ShowWindow(handle, SwShowNoActivate);
        if (!SetWindowPos(
                handle, IntPtr.Zero, -32000, -32000, 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Windows could not preserve the private Wallpaper Engine render surface position.");
        }

        if (!IsPrivateRenderExtendedStyle(ReadExtendedWindowStyle(handle)))
        {
            throw new InvalidOperationException(
                "Wallpaper Engine's private render window could not be excluded from the taskbar and Alt+Tab.");
        }
    }

    private static long ReadExtendedWindowStyle(IntPtr handle)
    {
        Marshal.SetLastPInvokeError(0);
        var value = GetWindowLongPtr(handle, GwlExStyle);
        var error = Marshal.GetLastPInvokeError();
        if (value == IntPtr.Zero && error != 0)
        {
            throw new Win32Exception(error,
                "Windows could not read the Wallpaper Engine render window style.");
        }
        return value.ToInt64();
    }

    internal static long ToPrivateRenderExtendedStyle(long currentStyle) =>
        (currentStyle | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;

    internal static bool IsPrivateRenderExtendedStyle(long style) =>
        (style & WsExToolWindow) != 0
        && (style & WsExNoActivate) != 0
        && (style & WsExAppWindow) == 0;

    public bool IsExcludedFromTaskSwitcher =>
        !_disposed
        && IsWindow(_windowHandle)
        && IsPrivateRenderExtendedStyle(ReadExtendedWindowStyle(_windowHandle));

    public bool IsRenderWindowAlive => IsWindow(_windowHandle);

    private static bool TryResolveEngine(string projectPath, out string engineRoot, out string executable)
    {
        engineRoot = string.Empty;
        executable = string.Empty;
        try
        {
            var current = new FileInfo(Path.GetFullPath(projectPath)).Directory;
            for (var depth = 0; depth < 10 && current is not null; depth++, current = current.Parent)
            {
                if (!current.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
                var root = Path.Combine(current.FullName, "common", "wallpaper_engine");
                foreach (var name in new[] { "wallpaper64.exe", "wallpaper32.exe" })
                {
                    var candidate = Path.Combine(root, name);
                    if (File.Exists(candidate))
                    {
                        engineRoot = Path.GetFullPath(root);
                        executable = Path.GetFullPath(candidate);
                        return true;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return false;
    }

    private static async Task RunControlAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Wallpaper Engine control process could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Wallpaper Engine did not accept the control command within 12 seconds.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Wallpaper Engine rejected a control command (exit {process.ExitCode}).");
        }
    }

    private static async Task<string> EnsureEngineRunningAsync(
        string engineRoot,
        string executable,
        CancellationToken cancellationToken)
    {
        var runningExecutable = FindRunningEngine(engineRoot);
        if (runningExecutable is not null) return runningExecutable;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-silent");
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Wallpaper Engine could not be started for high-fidelity Scene rendering.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            runningExecutable = FindRunningEngine(engineRoot);
            if (runningExecutable is not null) return runningExecutable;
            await Task.Delay(150, cancellationToken);
        }
        throw new TimeoutException("Wallpaper Engine did not start within 15 seconds.");
    }

    private static string? FindRunningEngine(string engineRoot)
    {
        foreach (var processName in new[] { "wallpaper64", "wallpaper32" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path)
                            && Path.GetFullPath(path).StartsWith(
                                Path.GetFullPath(engineRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return Path.GetFullPath(path);
                        }
                    }
                    catch { }
                }
            }
        }
        return null;
    }

    private static async Task RunControlWithStartupRetryAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RunControlAsync(executable, arguments, cancellationToken);
                return;
            }
            catch (InvalidOperationException exception)
            {
                lastFailure = exception;
                await Task.Delay(250, cancellationToken);
            }
        }
        throw new TimeoutException("Wallpaper Engine did not accept the high-fidelity render request within 15 seconds.", lastFailure);
    }

    private static async Task RunPropertyControlWithRetryAsync(
        string executable,
        string json,
        string windowName,
        CancellationToken cancellationToken)
    {
        if (json.Length > 64 * 1024
            || json.Contains(")~END", StringComparison.Ordinal)
            || windowName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_')))
        {
            throw new InvalidDataException("Wallpaper Engine property data failed command-line validation.");
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // Wallpaper Engine's RAW~(...)~END parser reads the unescaped
                // command line. ProcessStartInfo.ArgumentList escapes the JSON
                // and is rejected with exit code 4, so use the documented raw
                // syntax after tightly validating every interpolated value.
                Arguments = $"-control applyProperties -properties RAW~({json})~END -location \"{windowName}\""
            };
            try
            {
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Wallpaper Engine property control could not be started.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode == 0) return;
                lastFailure = new InvalidOperationException(
                    $"Wallpaper Engine rejected the property update (exit {process.ExitCode}).");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = new TimeoutException("Wallpaper Engine property control timed out.");
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("Wallpaper Engine did not accept the property update within 15 seconds.", lastFailure);
    }

    private static async Task TryCloseWindowAsync(string executable, string windowName, IntPtr handle)
    {
        try
        {
            await RunControlAsync(executable,
                ["-control", "closeWallpaper", "-location", windowName], CancellationToken.None);
        }
        catch
        {
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (IsWindow(handle) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        if (IsWindow(handle))
        {
            var engineRoot = Path.GetDirectoryName(executable);
            if (!string.IsNullOrWhiteSpace(engineRoot)
                && IsExpectedWallpaperEngineWindow(handle, engineRoot))
            {
                PostMessage(handle, WmClose, 0, 0);
            }
        }

        deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (IsWindow(handle) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    private static int NormalizeFrameRate(int requested) => requested >= 60 ? 60 : 30;

    private static int? ReadConfiguredFrameRateLimit(string engineRoot)
    {
        try
        {
            var path = Path.Combine(engineRoot, "config.json");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > 2 * 1024 * 1024
                || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
            return FindFrameRate(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static int? FindFrameRate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("fps", StringComparison.OrdinalIgnoreCase)
                    && property.Value.TryGetInt32(out var fps)
                    && fps is >= 1 and <= 240) return fps;
                var nested = FindFrameRate(property.Value);
                if (nested.HasValue) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFrameRate(item);
                if (nested.HasValue) return nested;
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Let an in-flight CDP evaluation finish normally. Cancelling a
        // ClientWebSocket receive during every wallpaper switch can abort the
        // shared control socket and leave the page frozen on its last frame.
        Volatile.Write(ref _stopRequested, 1);
        if (_streamTask is not null)
        {
            try
            {
                await _streamTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                _lifetime.Cancel();
                try { await _streamTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            }
        }
        _lifetime.Cancel();
        var graphicsCapture = Interlocked.Exchange(ref _graphicsCapture, null);
        if (graphicsCapture is not null) await graphicsCapture.DisposeAsync();
        await TryCloseWindowAsync(_engineExecutable, _windowName, _windowHandle);
        _lifetime.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr targetDc, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);
}

internal static class CapturedFrameQuality
{
    private const int SampleColumns = 32;
    private const int SampleRows = 20;

    public static bool IsAcceptable(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth < 64 || source.PixelHeight < 64) return false;

        var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride];

        double sum = 0, sumSquares = 0, chroma = 0;
        var minimum = 255d;
        var maximum = 0d;
        var count = 0;
        for (var row = 0; row < SampleRows; row++)
        {
            var y = Math.Min(converted.PixelHeight - 1,
                (int)Math.Round((row + .5) * converted.PixelHeight / SampleRows - .5));
            converted.CopyPixels(new Int32Rect(0, y, converted.PixelWidth, 1), pixels, stride, 0);
            for (var column = 0; column < SampleColumns; column++)
            {
                var x = Math.Min(converted.PixelWidth - 1,
                    (int)Math.Round((column + .5) * converted.PixelWidth / SampleColumns - .5));
                var offset = x * 4;
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                var luminance = red * .2126 + green * .7152 + blue * .0722;
                sum += luminance;
                sumSquares += luminance * luminance;
                chroma += Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
                count++;
            }
        }

        var mean = sum / count;
        var variance = Math.Max(0, sumSquares / count - mean * mean);
        var deviation = Math.Sqrt(variance);
        var averageChroma = chroma / count;
        var dynamicRange = maximum - minimum;

        // PrintWindow intermittently yields a uniform black/white/gray surface
        // while the real DirectX frame is being presented. Never publish that
        // transient over the last known-good frame.
        return !(deviation < 2.25 && dynamicRange < 8 && averageChroma < 2.5);
    }

    public static bool IsAcceptable(CapturedBgraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width < 64 || frame.Height < 64 || frame.Stride < frame.Width * 4
            || frame.Pixels.Length < checked(frame.Stride * frame.Height)) return false;
        double sum = 0, sumSquares = 0, chroma = 0;
        var minimum = 255d;
        var maximum = 0d;
        var count = 0;
        for (var row = 0; row < SampleRows; row++)
        {
            var y = Math.Min(frame.Height - 1,
                (int)Math.Round((row + .5) * frame.Height / SampleRows - .5));
            for (var column = 0; column < SampleColumns; column++)
            {
                var x = Math.Min(frame.Width - 1,
                    (int)Math.Round((column + .5) * frame.Width / SampleColumns - .5));
                var offset = y * frame.Stride + x * 4;
                var blue = frame.Pixels[offset];
                var green = frame.Pixels[offset + 1];
                var red = frame.Pixels[offset + 2];
                var luminance = red * .2126 + green * .7152 + blue * .0722;
                sum += luminance;
                sumSquares += luminance * luminance;
                chroma += Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
                count++;
            }
        }
        var mean = sum / count;
        var deviation = Math.Sqrt(Math.Max(0, sumSquares / count - mean * mean));
        return !(deviation < 2.25 && maximum - minimum < 8 && chroma / count < 2.5);
    }
}

internal static class WallpaperEnginePropertyReader
{
    public static Dictionary<string, object?> Read(WallpaperEntry wallpaper, WallpaperSettings settings)
    {
        // The official renderer automatically loads every default declared by
        // project.json. Re-sending that complete declaration as applyProperties
        // is both redundant and rejected by Wallpaper Engine for read-only or
        // script-owned fields. Only send the user's saved overrides plus the
        // controller's playback/audio adjustments.
        var values = new Dictionary<string, object?>(
            ReadSavedOverrides(wallpaper.ProjectPath ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(wallpaper.ProjectPath)) return values;
        var baselineRate = values.TryGetValue("rate", out var configuredRate)
            && double.TryParse(Convert.ToString(configuredRate, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedRate)
            ? parsedRate
            : 100;
        values["rate"] = (int)Math.Round(Math.Clamp(baselineRate * settings.PlaybackRate, 10, 200));
        return values;
    }

    private static Dictionary<string, object?> ReadSavedOverrides(string projectPath)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var projectDirectory = new FileInfo(Path.GetFullPath(projectPath)).Directory;
            DirectoryInfo? steamApps = projectDirectory;
            for (var depth = 0; depth < 10 && steamApps is not null; depth++, steamApps = steamApps.Parent)
            {
                if (!steamApps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
                var configPath = Path.Combine(steamApps.FullName, "common", "wallpaper_engine", "config.json");
                if (!File.Exists(configPath)) return result;
                using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
                var packagePath = Path.Combine(projectDirectory!.FullName, "scene.pkg").Replace('\\', '/');
                foreach (var profile in document.RootElement.EnumerateObject())
                {
                    if (profile.Name.StartsWith("?", StringComparison.Ordinal)
                        || profile.Value.ValueKind != JsonValueKind.Object
                        || !profile.Value.TryGetProperty("wproperties", out var wallpaperProperties)
                        || wallpaperProperties.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    foreach (var item in wallpaperProperties.EnumerateObject())
                    {
                        if (!item.Name.Replace('\\', '/').Equals(packagePath, StringComparison.OrdinalIgnoreCase)
                            || item.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }
                        foreach (var monitor in item.Value.EnumerateObject())
                        {
                            if (monitor.Value.ValueKind != JsonValueKind.Object) continue;
                            foreach (var property in monitor.Value.EnumerateObject())
                            {
                                var value = ToValue(property.Value);
                                if (IsSafeProperty(property.Name, value)) result[property.Name] = value;
                            }
                            break;
                        }
                    }
                }
                return result;
            }
        }
        catch
        {
        }
        return result;
    }

    private static bool IsSafeProperty(string name, object? value)
    {
        if (name.Length is < 1 or > 64
            || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            return false;
        }
        return value switch
        {
            null => false,
            bool => true,
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => true,
            string text => text.Length <= 2048 && !text.Contains(")~END", StringComparison.Ordinal),
            _ => false
        };
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.Clone()
    };
}
