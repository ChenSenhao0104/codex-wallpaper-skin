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

/// <summary>
/// Uses Wallpaper Engine itself as the renderer for Scene projects. Frames are
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

    /// <summary>Retries allowed for one fragment batch when the page reports a saturated transport.</summary>
    private const int MaximumTransportRetries = 3;
    private readonly string _engineExecutable;
    private readonly string _windowName;
    private readonly IntPtr _windowHandle;
    private readonly CancellationTokenSource _lifetime = new();
    private WindowsGraphicsCaptureSource? _graphicsCapture;
    private GpuMediaPipeline? _gpuPipeline;
    private Task? _streamTask;
    private int _frameRate;
    private bool _pauseWhenHidden;
    private volatile bool _lastPageHidden;
    private byte[] _lastEncodedFrame;
    private readonly double _baseRate;
    private readonly double _baseVolume;
    private readonly int _requestedFrameRate;
    private int _lastPointerButtons;
    private int _stopRequested;
    private string? _gpuFailure;
    private string? _gpuCodec;
    private int _recovering;
    private int _engineFrameRateCap;
    private bool _disposed;

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
        GpuMediaPipeline? gpuPipeline = null,
        byte[]? gpuInitialFrame = null,
        string? gpuStartFailure = null,
        int engineFrameRateCap = 0)
    {
        _engineFrameRateCap = engineFrameRateCap;
        _engineExecutable = engineExecutable;
        _windowName = windowName;
        _windowHandle = windowHandle;
        _graphicsCapture = graphicsCapture;
        _gpuPipeline = gpuPipeline;
        _gpuCodec = gpuPipeline?.Codec;
        _gpuFailure = gpuStartFailure;
        InitialFrame = gpuInitialFrame ?? initialFrame;
        _lastEncodedFrame = InitialFrame;
        _frameRate = NormalizeFrameRate(frameRate);
        // The product mode the user asked for, kept separate from the compatibility
        // backend's own 10/15 pacing so a status claim can never confuse the two.
        _requestedFrameRate = GpuStreamStatusLabel.NormalizeFrameRate(frameRate);
        _pauseWhenHidden = pauseWhenHidden;
        _baseRate = baseRate;
        _baseVolume = baseVolume;
        // Published immediately, not only after streaming starts, so the controller
        // can show why the GPU path is not running at the requested rate.
        UpdateGpuStatus();
    }

    public byte[] InitialFrame { get; }
    public bool UsesWindowsGraphicsCapture => _graphicsCapture is not null;

    /// <summary>True when the v0.4 GPU media path owns this session.</summary>
    public bool UsesGpuMediaPath => _gpuPipeline is not null;

    /// <summary>
    /// Status label required by Issue #2, shared by the controller window, Doctor
    /// and the acceptance report. Derived from <see cref="Status"/> so the pair can
    /// never disagree, and initialised conservatively until the first update.
    /// </summary>
    public string StatusLabel => GpuStreamStatusLabel.Describe(Status);

    public GpuStreamStatus Status { get; private set; } = GpuStreamStatus.UnsupportedOrFailed;

    /// <summary>Exact Media Source Extensions codec string reported by the encoder, when the GPU path is active.</summary>
    public string? GpuCodec => _gpuCodec ?? _gpuPipeline?.Codec;

    /// <summary>Capture geometry and cadence of the GPU path, reported to the page so status matches reality.</summary>
    public int GpuWidth => _gpuPipeline?.Width ?? 0;

    public int GpuHeight => _gpuPipeline?.Height ?? 0;

    public int GpuFrameRate => _gpuPipeline?.FrameRate ?? _frameRate;

    public string? GpuFailureReason => _gpuFailure;

    public GpuStreamDiagnosticsSnapshot? GpuDiagnostics => _gpuPipeline?.Snapshot(Status, _gpuFailure);

    /// <summary>Why the GPU media path was not used, when the compatibility backend was selected instead.</summary>
    public string? GpuStartFailureReason => _gpuFailure;

    public bool IsRunning => !_disposed && _streamTask is { IsCompleted: false };
    public Task Completion => _streamTask ?? Task.CompletedTask;


    public static bool CanUse(WallpaperEntry wallpaper) =>
        wallpaper.IsWallpaperEngineScene
        && !string.IsNullOrWhiteSpace(wallpaper.ProjectPath)
        && TryResolveEngine(wallpaper.ProjectPath, out _, out _);

    public static async Task<WallpaperEngineCaptureSession> StartAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        int viewportWidth,
        int viewportHeight,
        CancellationToken cancellationToken = default,
        bool useGpuMediaPath = false,
        bool skipInternalCapture = false)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        ArgumentNullException.ThrowIfNull(settings);
        if (!wallpaper.IsWallpaperEngineScene || string.IsNullOrWhiteSpace(wallpaper.ProjectPath))
        {
            throw new InvalidDataException("Wallpaper Engine capture requires a contained Scene project.");
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

        var scale = Math.Clamp(settings.SceneResolutionScale, 0.5, 1);
        var width = Math.Clamp((int)Math.Round(Math.Max(960, viewportWidth) * scale), 960, 1920);
        var height = Math.Clamp((int)Math.Round(Math.Max(600, viewportHeight) * scale), 600, 1200);
        var windowName = "Codex Wallpaper Skin " + Guid.NewGuid().ToString("N");
        WallpaperEnginePropertyReader.TryReadFrameRateCap(projectPath, out var engineFrameRateCap);
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

            // A diagnostics probe can own the capture itself, in which case the
            // session still creates and owns the private render window but does not
            // attach a second capture session to it.
            var graphicsCapture = skipInternalCapture ? null : WindowsGraphicsCaptureSource.TryStart(handle);
            byte[] initialFrame;
            string? gpuStartFailure = null;
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

            if (useGpuMediaPath)
            {
                // The v0.4 production path is attempted before the reduced
                // frame-rate compatibility backend, and its failure is recorded
                // verbatim so the caller can report why the GPU path was not used.
                if (GpuMediaPipeline.TryStart(handle, settings, out var gpuPipeline, out var gpuFailure)
                    && gpuPipeline is not null)
                {
                    try
                    {
                        // TryStart already primed the encoder until the
                        // initialisation segment existed, so a missing codec here
                        // means the container is not streamable at all.
                        if (gpuPipeline.Codec is null)
                        {
                            gpuFailure = "The encoder produced no streamable initialisation segment.";
                        }
                        else
                        {
                            var gpuInitialFrame = EncodeFirstGpuFrame(gpuPipeline) ?? initialFrame;
                            if (graphicsCapture is not null)
                            {
                                // The GPU path replaces the bitmap capture session
                                // entirely, so the compatibility source is released
                                // before the session is handed to the caller.
                                await graphicsCapture.DisposeAsync();
                            }
                            graphicsCapture = null;
                            return new WallpaperEngineCaptureSession(
                                controlExecutable, windowName, handle, graphicsCapture, initialFrame,
                                settings.SceneFrameRate, settings.PauseWhenHidden, baseRate, baseVolume,
                                gpuPipeline, gpuInitialFrame, gpuStartFailure: null, engineFrameRateCap: engineFrameRateCap);
                        }
                    }
                    catch (Exception exception)
                    {
                        gpuFailure = exception.Message;
                    }
                    gpuPipeline.Dispose();
                }
                else if (string.IsNullOrWhiteSpace(gpuFailure))
                {
                    gpuFailure = "The GPU media pipeline is unavailable on this system.";
                }
                gpuStartFailure = gpuFailure;
            }

            return new WallpaperEngineCaptureSession(
                controlExecutable, windowName, handle, graphicsCapture, initialFrame, settings.SceneFrameRate,
                settings.PauseWhenHidden, baseRate, baseVolume, gpuPipeline: null, gpuInitialFrame: null,
                gpuStartFailure: gpuStartFailure, engineFrameRateCap: engineFrameRateCap);
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
        _streamTask = Task.WhenAll(
            Task.Run(() => StreamFramesAsync(publishFrame, _lifetime.Token)),
            Task.Run(() => StreamPointerAsync(readPointer, _lifetime.Token)));
    }

    /// <summary>
    /// Starts the v0.4 GPU media loop. Frames are captured, hardware encoded and
    /// delivered as bounded fragment batches; delivery is acknowledged by the
    /// page, so the controller never reports a wallpaper switch the user has not
    /// seen. Recovery from a transient transport refusal is bounded, backed off,
    /// and never starts a competing worker.
    /// </summary>
    internal void StartStreamingGpu(
        Func<GpuFrameBatch, CancellationToken, Task<string>> publishBatch,
        Func<CancellationToken, Task<CapturedPointer?>> readPointer)
    {
        ArgumentNullException.ThrowIfNull(publishBatch);
        ArgumentNullException.ThrowIfNull(readPointer);
        if (_disposed) throw new ObjectDisposedException(nameof(WallpaperEngineCaptureSession));
        if (_gpuPipeline is null) throw new InvalidOperationException("This session is not using the GPU media path.");
        if (_streamTask is not null) throw new InvalidOperationException("Wallpaper Engine capture is already streaming.");
        _streamTask = Task.WhenAll(
            // Capture and encode must run independently of transport pacing. A
            // live measurement showed a single combined loop consuming only about
            // 17 frames per second, because every transport decision cost the
            // capture a frame; the encoder is now fed by its own loop and the
            // transport only drains what the encoder produced.
            Task.Run(() => FeedGpuFramesAsync(_lifetime.Token)),
            Task.Run(() => PublishGpuBatchesAsync(publishBatch, _lifetime.Token)),
            Task.Run(() => StreamPointerAsync(readPointer, _lifetime.Token)));
    }

    /// <summary>
    /// Keeps the encoder fed from the capture source at the capture cadence. It
    /// never awaits the transport, so a slow consumer throttles through the
    /// encoder's bounded queue and the capture channel's drop-oldest policy
    /// instead of starving the feed.
    /// </summary>
    private async Task FeedGpuFramesAsync(CancellationToken cancellationToken)
    {
        var pipeline = _gpuPipeline;
        if (pipeline is null)
        {
            return;
        }
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
                if (!pipeline.CaptureAndSubmit(16, out var captureFailure))
                {
                    // A capture or encoder failure invalidates the container, so
                    // the GPU path stops with a diagnosed reason and the last
                    // confirmed good frame stays on screen.
                    _gpuFailure = captureFailure;
                    Volatile.Write(ref _stopRequested, 1);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _gpuFailure = "The GPU capture loop failed: " + exception.Message;
            Volatile.Write(ref _stopRequested, 1);
        }
        finally
        {
            UpdateGpuStatus();
        }
    }

    /// <summary>Drains encoder output, batches it and delivers it with backpressure from the page.</summary>
    private async Task PublishGpuBatchesAsync(
        Func<GpuFrameBatch, CancellationToken, Task<string>> publishBatch,
        CancellationToken cancellationToken)
    {
        var pipeline = _gpuPipeline;
        if (pipeline is null)
        {
            return;
        }
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0
                && !cancellationToken.IsCancellationRequested
                && IsWindow(_windowHandle))
            {
                // Waiting on the encoder queue is the pacing signal: no spin, no
                // fixed sleep, and a fragment is delivered as soon as it exists.
                pipeline.DrainFragments(15, out _);
                var now = DateTimeOffset.UtcNow;
                if (!pipeline.ShouldFlushBatch(now))
                {
                    continue;
                }
                var batch = pipeline.TakeBatch(now);
                if (batch is null)
                {
                    continue;
                }
                if (await DeliverAsync(pipeline, batch, publishBatch, cancellationToken) is null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            UpdateGpuStatus();
        }
    }

    /// <summary>
    /// Delivers one batch, retrying only the condition that is safe to retry. A
    /// 'busy' answer means the page rejected the batch without consuming its
    /// sequence numbers, so resending the same batch is idempotent; every other
    /// refusal is terminal and is reported instead of being hidden by a retry
    /// loop.
    /// </summary>
    private async Task<string?> DeliverAsync(
        GpuMediaPipeline pipeline,
        GpuFrameBatch batch,
        Func<GpuFrameBatch, CancellationToken, Task<string>> publishBatch,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            string delivery;
            try
            {
                delivery = await publishBatch(batch, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                delivery = "transport-error";
                _gpuFailure = "The GPU media transport failed: " + exception.Message;
            }
            pipeline.ReportDelivery(batch, delivery, DateTimeOffset.UtcNow);

            if (delivery == "busy" && attempt < MaximumTransportRetries)
            {
                Volatile.Write(ref _recovering, 1);
                UpdateGpuStatus();
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken);
                continue;
            }
            Volatile.Write(ref _recovering, 0);

            if (delivery is "presented" or "appended")
            {
                return delivery;
            }
            _gpuFailure = delivery switch
            {
                "busy" => $"The page did not accept GPU fragments after {MaximumTransportRetries + 1} attempts.",
                "stale" => "Another controller replaced the GPU stream.",
                "rejected" => "The page rejected a GPU fragment as out of order or malformed.",
                "decode-failed" => "The page could not decode the GPU media stream.",
                _ => "The GPU media transport reported: " + delivery
            };
            Volatile.Write(ref _stopRequested, 1);
            UpdateGpuStatus();
            return null;
        }
    }

    private void UpdateGpuStatus()
    {
        var recovering = Volatile.Read(ref _recovering) != 0;
        var pipeline = _gpuPipeline;
        var declared = pipeline?.FrameRate ?? _frameRate;
        var requested = _requestedFrameRate;
        // The label follows the cadence of frames that really came from the source
        // once there is enough media to measure it. The coded cadence is not used:
        // Media Foundation resamples the timeline to the declared rate, so it can
        // include repeated frames and would overstate what the user sees.
        var measured = pipeline?.MeasuredSourceFps ?? 0;
        var effective = measured >= 1 ? (int)Math.Round(measured) : declared;
        Status = GpuStreamStatusLabel.Decide(new GpuStreamStatusInput(
            GpuPathActive: pipeline is not null && _gpuFailure is null,
            Recovering: recovering,
            RequestedFrameRate: effective,
            CompatibilityCaptureAvailable: true,
            StaticFallbackAvailable: !string.IsNullOrWhiteSpace(_gpuFailure)));
        if (_gpuFailure is null && pipeline is not null
            && GpuStreamStatusLabel.IsBelowRequested(effective, requested))
        {
            var cap = Volatile.Read(ref _engineFrameRateCap);
            var cappedNote = cap > 0 && Math.Abs(effective - cap) <= Math.Max(2, cap * 0.15)
                ? $"Wallpaper Engine is configured to {cap} FPS in its own settings, which caps the captured cadence; "
                    + "raise that setting for a higher rate"
                : $"the Wallpaper Engine capture surface sustains about {effective} frames per second at "
                    + $"{pipeline.Width}x{pipeline.Height}; the Scene itself is the limit, and the rate is "
                    + "independent of the render scale";
            GpuStatusNote = cappedNote
                + $" (this session runs a {effective} FPS GPU mode, not the {requested} FPS target)";
        }
        else
        {
            GpuStatusNote = _gpuFailure;
        }
    }

    /// <summary>Human-readable reason the status is not the 60 FPS target, when there is one.</summary>
    public string? GpuStatusNote { get; private set; }

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

    /// <summary>
    /// Paints one still frame from the first GPU capture so the user sees the
    /// wallpaper immediately while the Media Source surface initialises. It is
    /// the same quality gate and JPEG encoder the compatibility backend uses.
    /// </summary>
    private static byte[]? EncodeFirstGpuFrame(GpuMediaPipeline pipeline)
    {
        var pixels = pipeline.TakeFirstFramePixels();
        if (pixels is null)
        {
            return null;
        }
        try
        {
            var bitmap = BitmapSource.Create(
                pipeline.Width, pipeline.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, pipeline.Width * 4);
            bitmap.Freeze();
            return EncodeCapturedFrame(bitmap);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<byte[]> CaptureFirstGoodFrameAsync(IntPtr handle, CancellationToken cancellationToken)    {
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

    /// <summary>The private render window this session owns, for diagnostics that need to attach their own capture.</summary>
    public IntPtr RenderWindowHandle => _windowHandle;

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

    private static int NormalizeFrameRate(int requested) => requested <= 10 ? 10 : 15;

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
        var gpuPipeline = Interlocked.Exchange(ref _gpuPipeline, null);
        gpuPipeline?.Dispose();
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

    /// <summary>
    /// Rejects empty, uniform black/gray/white and implausibly small frames.
    ///
    /// Windows Graphics Capture intermittently yields a uniform surface while the
    /// real DirectX frame is being presented, and the GPU path must reject that
    /// transient before it reaches the encoder, otherwise the user sees a flash.
    /// </summary>
    public static bool IsAcceptable(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width < 64 || height < 64 || bgra.Length < (long)width * height * 4)
        {
            return false;
        }

        double sum = 0, sumSquares = 0, chroma = 0;
        var minimum = 255d;
        var maximum = 0d;
        var count = 0;
        for (var row = 0; row < SampleRows; row++)
        {
            var y = Math.Min(height - 1, (int)Math.Round(((row + .5) * height / SampleRows) - .5));
            var rowOffset = y * width * 4;
            for (var column = 0; column < SampleColumns; column++)
            {
                var x = Math.Min(width - 1, (int)Math.Round(((column + .5) * width / SampleColumns) - .5));
                var offset = rowOffset + (x * 4);
                double blue = bgra[offset], green = bgra[offset + 1], red = bgra[offset + 2];
                var luminance = (red * .2126) + (green * .7152) + (blue * .0722);
                sum += luminance;
                sumSquares += luminance * luminance;
                chroma += Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
                count++;
            }
        }
        return count > 0 && IsVaried(sum, sumSquares, chroma, minimum, maximum, count);
    }

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
        return count > 0 && IsVaried(sum, sumSquares, chroma, minimum, maximum, count);
    }

    /// <summary>
    /// PrintWindow and Windows Graphics Capture both intermittently yield a
    /// uniform black/white/gray surface while the real DirectX frame is being
    /// presented. Never publish that transient over the last known-good frame.
    /// </summary>
    private static bool IsVaried(double sum, double sumSquares, double chroma, double minimum, double maximum, int count)
    {
        var mean = sum / count;
        var variance = Math.Max(0, (sumSquares / count) - (mean * mean));
        var deviation = Math.Sqrt(variance);
        var averageChroma = chroma / count;
        var dynamicRange = maximum - minimum;
        return !(deviation < 2.25 && dynamicRange < 8 && averageChroma < 2.5);
    }
}

internal static class WallpaperEnginePropertyReader
{
    private const long MaximumConfigBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Reads Wallpaper Engine's own global frame rate limit ("fps" under
    /// general/user) from the same config.json the property reader already
    /// consults.
    ///
    /// This matters for honest status: when the capture rate sits exactly at that
    /// limit, the limit is the reason the GPU path cannot reach 60 FPS, and the
    /// user can raise it. Reporting "reduce the render scale" instead would be
    /// wrong, because the measurement shows the rate is independent of resolution.
    /// Never throws and never returns a path.
    /// </summary>
    internal static bool TryReadFrameRateCap(string projectPath, out int framesPerSecond)
    {
        framesPerSecond = 0;
        try
        {
            var configPath = FindEngineConfigPath(projectPath);
            if (configPath is null)
            {
                return false;
            }
            var info = new FileInfo(configPath);
            if (!info.Exists || info.Length is <= 0 or > MaximumConfigBytes)
            {
                return false;
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            foreach (var profile in document.RootElement.EnumerateObject())
            {
                if (profile.Value.ValueKind != JsonValueKind.Object
                    || !TryGetPropertyIgnoreCase(profile.Value, "general", out var general)
                    || !TryGetPropertyIgnoreCase(general, "user", out var user)
                    || !TryGetPropertyIgnoreCase(user, "fps", out var fps))
                {
                    continue;
                }
                if (fps.ValueKind == JsonValueKind.Number
                    && fps.TryGetInt32(out var value)
                    && value is >= 1 and <= 360)
                {
                    framesPerSecond = value;
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    /// <summary>Locates the installed engine's config.json without exposing the path to any diagnostic.</summary>
    private static string? FindEngineConfigPath(string projectPath)
    {
        var directory = new FileInfo(Path.GetFullPath(projectPath)).Directory;
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (!directory.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var candidate = Path.Combine(directory.FullName, "common", "wallpaper_engine", "config.json");
            return File.Exists(candidate) ? candidate : null;
        }
        return null;
    }

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
