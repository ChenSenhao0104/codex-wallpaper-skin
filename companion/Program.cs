using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace CodexWallpaperSkin;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            ConsoleBridge.Attach();
            return RunCliAsync(args).GetAwaiter().GetResult();
        }

        using var instanceMutex = new Mutex(false, @"Local\CodexWallpaperSkin.Companion.Gui");
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = instanceMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex)
            {
                MessageBox.Show(
                    "Codex Wallpaper Skin is already open. Use the existing adjustment window.",
                    "Codex Wallpaper Skin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return 0;
            }

            // A hidden handoff worker from the previous GUI session must yield
            // before this interactive controller can become authoritative.
            DeferredRestoreLauncher.RequestStop();

            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var mainWindow = new MainWindow();
            application.SessionEnding += (_, _) => mainWindow.PrepareForSystemShutdown();
            return application.Run(mainWindow);
        }
        finally
        {
            if (ownsMutex)
            {
                instanceMutex.ReleaseMutex();
            }
        }
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        try
        {
            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            {
                var result = SelfTests.Run();
                foreach (var message in result.Messages)
                {
                    Console.WriteLine(message);
                }
                Console.WriteLine($"Self-test: {result.Passed} passed, {result.Failed} failed.");
                return result.Success ? 0 : 1;
            }

            if (args.Contains("--wgc-smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                Console.WriteLine(await WgcSmokeTest.RunAsync(timeout.Token));
                return 0;
            }

            if (args.Contains("--browser-media-probe", StringComparer.OrdinalIgnoreCase))
            {
                var state = StateStore.Load();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await using var injection = new CdpInjectionService();
                await injection.ConnectAsync(state.CdpBaseUrl, timeout.Token);
                var report = await injection.ProbeBrowserMediaAsync(timeout.Token);
                Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                return report.LoopbackWebSocket && report.VideoDecoder && report.H264DecoderConfiguration ? 0 : 3;
            }

            if (args.Contains("--h264-encoder-probe", StringComparer.OrdinalIgnoreCase))
            {
                var report = MediaFoundationH264Probe.Run();
                Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                return report.Error is null && report.Encoders.Count > 0 ? 0 : 3;
            }

            if (args.Contains("--h264-encode-probe", StringComparer.OrdinalIgnoreCase))
            {
                const int width = 640;
                const int height = 360;
                const int frameRate = 30;
                var pixels = new byte[width * height * 4];
                var outputFrames = 0;
                var outputBytes = 0L;
                var keyFrames = 0;
                using var encoder = MediaFoundationH264Encoder.Create(width, height, frameRate, 3_000_000);
                for (var frame = 0; frame < 45; frame++)
                {
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var offset = (y * width + x) * 4;
                        pixels[offset] = (byte)((x + frame * 3) & 255);
                        pixels[offset + 1] = (byte)((y + frame * 2) & 255);
                        pixels[offset + 2] = (byte)((x + y + frame * 5) & 255);
                        pixels[offset + 3] = 255;
                    }
                    foreach (var encoded in encoder.EncodeBgra(pixels, width, height, width * 4))
                    {
                        outputFrames++;
                        outputBytes += encoded.Data.Length;
                        if (encoded.KeyFrame) keyFrames++;
                    }
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Width = width,
                    Height = height,
                    InputFrames = 45,
                    OutputFrames = outputFrames,
                    OutputBytes = outputBytes,
                    KeyFrames = keyFrames
                }, new JsonSerializerOptions { WriteIndented = true }));
                return outputFrames > 0 && outputBytes > 0 && keyFrames > 0 ? 0 : 3;
            }

            var h264BrowserTestIndex = Array.FindIndex(args,
                value => value.Equals("--h264-browser-smoke-test", StringComparison.OrdinalIgnoreCase));
            if (h264BrowserTestIndex >= 0)
            {
                if (h264BrowserTestIndex + 1 >= args.Length)
                    throw new ArgumentException("--h264-browser-smoke-test requires a project.json path.");
                var wallpaper = WallpaperCatalog.ParseProject(args[h264BrowserTestIndex + 1]);
                if (!wallpaper.IsWallpaperEngineScene)
                    throw new InvalidDataException("The H.264 browser smoke test accepts Wallpaper Engine Scene projects only.");
                var state = StateStore.Load();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await using var injection = new CdpInjectionService();
                try
                {
                    await injection.ConnectAsync(state.CdpBaseUrl, timeout.Token);
                    var result = await injection.ApplyAsync(
                        wallpaper,
                        new WallpaperSettings
                        {
                            Muted = true,
                            SceneFrameRate = 60,
                            SceneResolutionScale = .75,
                            PauseWhenHidden = false
                        },
                        cancellationToken: timeout.Token);
                    await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
                    var diagnostics = await injection.GetActiveStreamDiagnosticsAsync(timeout.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new { result.Mode, result.Warning, Diagnostics = diagnostics },
                        new JsonSerializerOptions { WriteIndented = true }));
                    return result.Mode == "wallpaper-engine-h264"
                        && diagnostics.Presented >= 30
                        && diagnostics.DecodeErrors == 0
                        ? 0 : 3;
                }
                finally
                {
                    try { await injection.CleanupAsync(CancellationToken.None); } catch { }
                }
            }

            var h264SwitchTestIndex = Array.FindIndex(args,
                value => value.Equals("--h264-switch-smoke-test", StringComparison.OrdinalIgnoreCase));
            if (h264SwitchTestIndex >= 0)
            {
                if (h264SwitchTestIndex + 3 >= args.Length)
                    throw new ArgumentException("--h264-switch-smoke-test requires three project.json paths.");
                var wallpapers = args.Skip(h264SwitchTestIndex + 1).Take(3)
                    .Select(WallpaperCatalog.ParseProject).ToArray();
                if (wallpapers.Any(wallpaper => !wallpaper.IsWallpaperEngineScene))
                    throw new InvalidDataException("The H.264 switch test accepts Wallpaper Engine Scene projects only.");
                var state = StateStore.Load();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await using var injection = new CdpInjectionService();
                var results = new List<object>();
                try
                {
                    await injection.ConnectAsync(state.CdpBaseUrl, timeout.Token);
                    for (var cycle = 0; cycle < 2; cycle++)
                    foreach (var wallpaper in wallpapers)
                    {
                        var result = await injection.ApplyAsync(
                            wallpaper,
                            new WallpaperSettings
                            {
                                Muted = true,
                                SceneFrameRate = 60,
                                SceneResolutionScale = .75,
                                PauseWhenHidden = false
                            },
                            cancellationToken: timeout.Token);
                        await Task.Delay(TimeSpan.FromMilliseconds(1200), timeout.Token);
                        var diagnostics = await injection.GetActiveStreamDiagnosticsAsync(timeout.Token);
                        results.Add(new
                        {
                            Cycle = cycle + 1,
                            Wallpaper = wallpaper.DisplayTitle,
                            result.Mode,
                            diagnostics.Presented,
                            diagnostics.DecodeErrors,
                            diagnostics.TransportError,
                            diagnostics.Native
                        });
                        if (result.Mode != "wallpaper-engine-h264"
                            || diagnostics.Presented < 1
                            || diagnostics.DecodeErrors != 0
                            || diagnostics.TransportError is not null)
                            throw new InvalidOperationException($"The H.264 switch test failed for {wallpaper.DisplayTitle}.");
                    }
                    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
                finally
                {
                    try { await injection.CleanupAsync(CancellationToken.None); } catch { }
                }
            }

            var wallpaperEngineTestIndex = Array.FindIndex(args,
                value => value.Equals("--we-capture-smoke-test", StringComparison.OrdinalIgnoreCase));
            var soakTest = false;
            if (wallpaperEngineTestIndex < 0)
            {
                wallpaperEngineTestIndex = Array.FindIndex(args,
                    value => value.Equals("--we-capture-soak-test", StringComparison.OrdinalIgnoreCase));
                soakTest = wallpaperEngineTestIndex >= 0;
            }
            if (wallpaperEngineTestIndex >= 0)
            {
                if (wallpaperEngineTestIndex + 1 >= args.Length)
                {
                    throw new ArgumentException("--we-capture-smoke-test requires a project.json path.");
                }
                var wallpaper = WallpaperCatalog.ParseProject(args[wallpaperEngineTestIndex + 1]);
                if (!wallpaper.IsWallpaperEngineScene)
                {
                    throw new InvalidDataException("The capture smoke test accepts Wallpaper Engine Scene projects only.");
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(soakTest ? 90 : 35));
                await using var session = await WallpaperEngineCaptureSession.StartAsync(
                    wallpaper,
                    new WallpaperSettings { Muted = true, SceneFrameRate = 10, SceneResolutionScale = .75 },
                    1600,
                    1000,
                    timeout.Token);
                if (!session.IsExcludedFromTaskSwitcher)
                {
                    throw new InvalidOperationException(
                        "Wallpaper Engine render window is still visible to the taskbar or Alt+Tab.");
                }
                var streamedFrames = 0;
                var pointerSamples = 0;
                session.StartStreaming(
                    (frame, _) =>
                    {
                        if (frame.Length > 0) Interlocked.Increment(ref streamedFrames);
                        return Task.CompletedTask;
                    },
                    _ =>
                    {
                        var sample = Interlocked.Increment(ref pointerSamples);
                        var phase = (sample % 90) / 89d;
                        var x = .1 + (.8 * phase);
                        var y = .5 + (.25 * Math.Sin(phase * Math.PI * 2));
                        var buttons = sample % 90 is >= 30 and <= 34 ? 1 : 0;
                        var wheel = sample % 90 == 45 ? 120 : 0;
                        return Task.FromResult<CapturedPointer?>(
                            new CapturedPointer(x, y, buttons, wheel, Hidden: false, Inside: true));
                    });
                var streamDeadline = DateTimeOffset.UtcNow.AddSeconds(soakTest ? 60 : 5);
                while (DateTimeOffset.UtcNow < streamDeadline
                    && (soakTest
                        || Volatile.Read(ref streamedFrames) < 3
                        || Volatile.Read(ref pointerSamples) < 50))
                {
                    await Task.Delay(100, timeout.Token);
                }
                var minimumFrames = soakTest ? 300 : 3;
                var minimumPointerSamples = soakTest ? 900 : 50;
                if (Volatile.Read(ref streamedFrames) < minimumFrames
                    || Volatile.Read(ref pointerSamples) < minimumPointerSamples)
                {
                    throw new TimeoutException(
                        $"Wallpaper Engine capture/input stopped below the test threshold: "
                        + $"{streamedFrames}/{minimumFrames} frames, {pointerSamples}/{minimumPointerSamples} pointer samples, "
                        + $"session running={session.IsRunning}.");
                }
                var backend = session.UsesWindowsGraphicsCapture ? "WGC/D3D11" : "compatibility";
                var initialFrameBytes = session.InitialFrame.Length;
                await session.DisposeAsync();
                if (session.IsRenderWindowAlive)
                {
                    throw new InvalidOperationException(
                        "Wallpaper Engine render window remained open after capture disposal.");
                }
                Console.WriteLine(
                    $"PASS Wallpaper Engine {(soakTest ? "60-second soak" : "capture/input")} ({backend}, {initialFrameBytes} initial bytes, {streamedFrames} streamed frames, {pointerSamples} pointer samples, private window released)");
                return 0;
            }

            var weH264TestIndex = Array.FindIndex(args,
                value => value.Equals("--we-h264-smoke-test", StringComparison.OrdinalIgnoreCase));
            if (weH264TestIndex >= 0)
            {
                if (weH264TestIndex + 1 >= args.Length)
                    throw new ArgumentException("--we-h264-smoke-test requires a project.json path.");
                var wallpaper = WallpaperCatalog.ParseProject(args[weH264TestIndex + 1]);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                await using var session = await WallpaperEngineCaptureSession.StartAsync(
                    wallpaper,
                    new WallpaperSettings { Muted = true, SceneFrameRate = 60, SceneResolutionScale = .75, PauseWhenHidden = false },
                    1600,
                    1000,
                    timeout.Token);
                var frames = 0;
                var bytes = 0L;
                var keyFrames = 0;
                session.StartH264Streaming(
                    (frame, _) =>
                    {
                        Interlocked.Increment(ref frames);
                        Interlocked.Add(ref bytes, frame.Data.Length);
                        if (frame.KeyFrame) Interlocked.Increment(ref keyFrames);
                        return Task.CompletedTask;
                    });
                var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
                while (DateTimeOffset.UtcNow < deadline && !session.Completion.IsCompleted)
                    await Task.Delay(100, timeout.Token);
                if (session.Completion.IsCompleted) await session.Completion;
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Frames = frames,
                    Bytes = bytes,
                    KeyFrames = keyFrames,
                    Running = session.IsRunning,
                    Metrics = session.StreamMetrics
                }, new JsonSerializerOptions { WriteIndented = true }));
                return frames >= 60 && bytes > 0 && keyFrames > 0 ? 0 : 3;
            }

            if (args.Contains("--doctor", StringComparer.OrdinalIgnoreCase))
            {
                var state = StateStore.Load();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                DiagnosticReport report;
                try
                {
                    report = await DiagnosticsService.RunAsync(state, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("Diagnostics exceeded the 15-second safety timeout.");
                }
                if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    PrintDoctor(report);
                }
                return report.CdpReachable ? 0 : 2;
            }

            if (args.Contains("--restore", StringComparer.OrdinalIgnoreCase))
            {
                var state = StateStore.Load();
                await using var injection = new CdpInjectionService();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                int cleanedPages;
                try
                {
                    cleanedPages = await injection.CleanupAllAsync(state.CdpBaseUrl, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("Restore exceeded the 35-second command timeout.");
                }
                Console.WriteLine($"Removed Codex Wallpaper Skin injection from {cleanedPages} Codex app page(s).");
                return 0;
            }

            if (args.Contains("--auto-restore", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--wait-and-restore", StringComparer.OrdinalIgnoreCase))
            {
                return await DeferredRestoreLauncher.RunAsync();
            }

            Console.Error.WriteLine("Usage: CodexWallpaperSkin [--doctor [--json] | --restore | --auto-restore | --wait-and-restore | --self-test | --browser-media-probe | --h264-encoder-probe | --h264-encode-probe | --h264-browser-smoke-test <project.json> | --h264-switch-smoke-test <project1.json> <project2.json> <project3.json> | --wgc-smoke-test | --we-capture-smoke-test <project.json> | --we-capture-soak-test <project.json> | --we-h264-smoke-test <project.json>]");
            return 64;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(args.Contains("--verbose", StringComparer.OrdinalIgnoreCase)
                ? exception.ToString()
                : exception.Message);
            return 1;
        }
    }

    private static void PrintDoctor(DiagnosticReport report)
    {
        Console.WriteLine($"CDP endpoint: {report.CdpEndpoint}");
        Console.WriteLine($"Loopback only: {report.CdpEndpointIsLoopback}");
        Console.WriteLine($"Reachable: {report.CdpReachable}");
        if (!string.IsNullOrWhiteSpace(report.CdpError))
        {
            Console.WriteLine("CDP error: " + report.CdpError);
        }
        Console.WriteLine($"Page targets: {report.Targets.Count}");
        Console.WriteLine($"AUMID candidates: {report.AumidCandidates.Count}");
        Console.WriteLine($"Requested Scene FPS: {report.RequestedSceneFrameRate}");
        Console.WriteLine($"Wallpaper Engine FPS limit: {report.WallpaperEngineFrameRateLimit?.ToString() ?? "unknown"}");
        Console.WriteLine($"Hardware H.264 available: {report.HardwareH264Available}");
        foreach (var encoder in report.HardwareH264Encoders)
        {
            Console.WriteLine("Hardware H.264 encoder: " + encoder);
        }
        if (!string.IsNullOrWhiteSpace(report.HardwareH264ProbeError))
        {
            Console.WriteLine("Hardware H.264 probe error: " + report.HardwareH264ProbeError);
        }
        foreach (var note in report.Notes)
        {
            Console.WriteLine("NOTE: " + note);
        }
    }
}

internal static class ConsoleBridge
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void Attach()
    {
        if (GetConsoleWindow() == IntPtr.Zero)
        {
            AttachConsole(AttachParentProcess);
        }
        try
        {
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(output);
            Console.SetError(error);
        }
        catch
        {
            // Exit codes still make CLI invocations machine-readable when no console is attached.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
