using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexWallpaperSkin;

public sealed record CapturedPointer(double X, double Y, bool Down, bool Hidden);

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
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const nuint MkLeftButton = 0x0001;
    private const int MaximumFrameBytes = 2 * 1024 * 1024;
    private const int MaximumConsecutiveRejections = 30;
    private readonly string _engineExecutable;
    private readonly string _windowName;
    private readonly IntPtr _windowHandle;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _streamTask;
    private int _frameRate;
    private bool _pauseWhenHidden;
    private bool _lastPageHidden;
    private readonly double _baseRate;
    private readonly double _baseVolume;
    private bool _lastPointerDown;
    private bool _disposed;
    private int _windowWidth;
    private int _windowHeight;
    private int _publishedFrames;
    private int _rejectedFrames;
    private string? _lastRejectionReason;

    private WallpaperEngineCaptureSession(
        string engineExecutable,
        string windowName,
        IntPtr windowHandle,
        byte[] initialFrame,
        int frameRate,
        bool pauseWhenHidden,
        double baseRate,
        double baseVolume,
        int windowWidth,
        int windowHeight)
    {
        _engineExecutable = engineExecutable;
        _windowName = windowName;
        _windowHandle = windowHandle;
        InitialFrame = initialFrame;
        _frameRate = NormalizeFrameRate(frameRate);
        _pauseWhenHidden = pauseWhenHidden;
        _baseRate = baseRate;
        _baseVolume = baseVolume;
        _windowWidth = windowWidth;
        _windowHeight = windowHeight;
    }

    public byte[] InitialFrame { get; }
    public bool IsRunning => !_disposed && _streamTask is { IsCompleted: false };
    public Task Completion => _streamTask ?? Task.CompletedTask;

    /// <summary>Frames accepted, published and rejected since streaming started.</summary>
    public int PublishedFrameCount => Volatile.Read(ref _publishedFrames);
    public int RejectedFrameCount => Volatile.Read(ref _rejectedFrames);
    public string LastRejectionReason => Volatile.Read(ref _lastRejectionReason) ?? string.Empty;

    public string MetricsSummary =>
        $"capture {_windowWidth}x{_windowHeight} at {_frameRate} FPS, {PublishedFrameCount} published, "
        + $"{RejectedFrameCount} rejected"
        + (LastRejectionReason.Length == 0 ? string.Empty : $" (last: {LastRejectionReason})");

    public static bool CanUse(WallpaperEntry wallpaper) =>
        wallpaper.IsScene
        && !string.IsNullOrWhiteSpace(wallpaper.ProjectPath)
        && TryResolveEngine(wallpaper.ProjectPath, out _, out _);

    public static async Task<WallpaperEngineCaptureSession> StartAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        int viewportWidth,
        int viewportHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        ArgumentNullException.ThrowIfNull(settings);
        if (!wallpaper.IsScene || string.IsNullOrWhiteSpace(wallpaper.ProjectPath))
        {
            throw new InvalidDataException("Wallpaper Engine capture requires a validated Scene project.");
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
        var controlExecutable = await EnsureEngineRunningAsync(engineRoot, executable, cancellationToken);
        await RunControlWithStartupRetryAsync(controlExecutable,
            ["-control", "openWallpaper", "-file", projectPath, "-playInWindow", windowName,
             "-width", width.ToString(), "-height", height.ToString(),
             "-x", "-32000", "-y", "-32000", "-borderless"], cancellationToken);

        var handle = await WaitForWindowAsync(windowName, engineRoot, cancellationToken);
        SetWindowPos(handle, IntPtr.Zero, -32000, -32000, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        try
        {
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

            await Task.Delay(350, cancellationToken);
            var initialFrame = await Task.Run(() => CaptureInitialFrame(handle), cancellationToken);
            return new WallpaperEngineCaptureSession(
                controlExecutable, windowName, handle, initialFrame, settings.SceneFrameRate,
                settings.PauseWhenHidden, baseRate, baseVolume, width, height);
        }
        catch
        {
            await TryCloseWindowAsync(controlExecutable, windowName);
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
        _streamTask = Task.Run(() => StreamAsync(publishFrame, readPointer, _lifetime.Token));
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

    private async Task StreamAsync(
        Func<byte[], CancellationToken, Task> publishFrame,
        Func<CancellationToken, Task<CapturedPointer?>> readPointer,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var consecutiveRejections = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsWindow(_windowHandle))
            {
                if (_pauseWhenHidden && _lastPageHidden)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken);
                }
                var started = Stopwatch.GetTimestamp();

                // Pointer input travels on its own channel so interaction never
                // waits for a successfully captured or accepted frame.
                try
                {
                    var pointer = await readPointer(cancellationToken);
                    if (pointer is not null)
                    {
                        if (!pointer.Hidden) ForwardPointer(pointer);
                        _lastPageHidden = pointer.Hidden;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Pointer loss alone must not end a healthy capture stream.
                }

                try
                {
                    var analysis = CaptureFrame(_windowHandle, out var frame);
                    if (frame is not null && analysis.Acceptable)
                    {
                        await publishFrame(frame, cancellationToken);
                        Interlocked.Increment(ref _publishedFrames);
                        Volatile.Write(ref _lastRejectionReason, null);
                        consecutiveRejections = 0;
                    }
                    else
                    {
                        // Empty, uniform and partial surfaces are never presented;
                        // the last known-good frame stays visible in Codex.
                        Interlocked.Increment(ref _rejectedFrames);
                        Volatile.Write(ref _lastRejectionReason, analysis.Reason);
                        consecutiveRejections++;
                        if (consecutiveRejections >= MaximumConsecutiveRejections)
                        {
                            break;
                        }
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
                    if (consecutiveFailures >= 3) break;
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

    private void ForwardPointer(CapturedPointer pointer)
    {
        if (!GetClientRect(_windowHandle, out var rect)) return;
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var x = Math.Clamp((int)Math.Round(pointer.X * (width - 1)), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(pointer.Y * (height - 1)), 0, height - 1);
        var lParam = (nint)((y << 16) | (x & 0xffff));
        var keyState = pointer.Down ? MkLeftButton : 0;
        PostMessage(_windowHandle, WmMouseMove, keyState, lParam);
        if (pointer.Down != _lastPointerDown)
        {
            PostMessage(_windowHandle, pointer.Down ? WmLeftButtonDown : WmLeftButtonUp, keyState, lParam);
            _lastPointerDown = pointer.Down;
        }
    }

    private static byte[] CaptureInitialFrame(IntPtr handle)
    {
        var analysis = CaptureFrame(handle, out var frame);
        if (frame is null)
        {
            throw new InvalidDataException(
                "Wallpaper Engine has not produced a usable first frame yet: " + analysis.Reason);
        }
        return frame;
    }

    /// <summary>
    /// Captures one surface and classifies it. <paramref name="frame"/> is null
    /// when the surface must not be presented; the caller keeps the previous one.
    /// </summary>
    private static FrameQuality CaptureFrame(IntPtr handle, out byte[]? frame)
    {
        frame = null;
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

            // Reject empty, uniform and stale surfaces before they reach Codex.
            var samples = SampleSurface(source);
            var quality = FrameQualityEvaluator.Evaluate(samples);
            if (!quality.Acceptable)
            {
                return quality;
            }

            var encoded = EncodeJpeg(source, 85);
            if (encoded.Length > MaximumFrameBytes) encoded = EncodeJpeg(source, 65);
            if (encoded.Length is <= 0 or > MaximumFrameBytes)
            {
                return quality with { Reason = "The captured frame exceeded the streaming budget." };
            }
            frame = encoded;
            return quality;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(handle, windowDc);
        }
    }

    /// <summary>
    /// Copies a bounded number of evenly spaced rows as tightly packed 32-bit
    /// BGRA. Sampling keeps the quality check cheap at 15 FPS on large surfaces.
    /// </summary>
    private static byte[] SampleSurface(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var source32 = source.Format == PixelFormats.Bgr32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        var rowCount = Math.Clamp(height, 1, 48);
        var stride = width * 4;
        var buffer = new byte[stride * rowCount];
        for (var index = 0; index < rowCount; index++)
        {
            var y = rowCount == 1 ? 0 : (int)((long)index * (height - 1) / (rowCount - 1));
            source32.CopyPixels(new Int32Rect(0, y, width, 1), buffer, stride, index * stride);
        }
        return buffer;
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

    private static async Task TryCloseWindowAsync(string executable, string windowName)
    {
        try
        {
            await RunControlAsync(executable,
                ["-control", "closeWallpaper", "-location", windowName], CancellationToken.None);
        }
        catch
        {
        }
    }

    private static int NormalizeFrameRate(int requested) => requested <= 10 ? 10 : 15;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_streamTask is not null)
        {
            try { await _streamTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        await TryCloseWindowAsync(_engineExecutable, _windowName);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr targetDc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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
