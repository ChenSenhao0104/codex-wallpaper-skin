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
                ShutdownMode = ShutdownMode.OnMainWindowClose
            };
            return application.Run(new MainWindow());
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

            if (args.Contains("--gpu-encoder-smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                var options = new GpuEncoderSmokeOptions(
                    Width: ReadOption(args, "--width", 1280),
                    Height: ReadOption(args, "--height", 720),
                    FrameRate: ReadOption(args, "--fps", 60),
                    Seconds: ReadDoubleOption(args, "--seconds", 3),
                    OutputPath: ReadStringOption(args, "--output"),
                    FragmentMilliseconds: ReadDoubleOption(args, "--fragment-ms", 100),
                    CreateOnly: args.Contains("--create-only", StringComparer.OrdinalIgnoreCase),
                    PreferHardware: !args.Contains("--no-hardware", StringComparer.OrdinalIgnoreCase),
                    LowLatency: !args.Contains("--no-low-latency", StringComparer.OrdinalIgnoreCase),
                    HintFragmentDuration: !args.Contains("--no-fragment-hint", StringComparer.OrdinalIgnoreCase));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                GpuEncoderSmokeResult result;
                try
                {
                    result = await GpuStreamSelfTest.RunEncoderSmokeTestAsync(options, timeout.Token, Console.WriteLine);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("The GPU encoder smoke test exceeded its 180-second safety timeout.");
                }
                foreach (var detail in result.Details)
                {
                    Console.WriteLine("  " + detail);
                }
                Console.WriteLine(result.Summary);
                return result.Passed ? 0 : 1;
            }

            var gpuStreamTestIndex = Array.FindIndex(args,
                value => value.Equals("--gpu-stream-smoke-test", StringComparison.OrdinalIgnoreCase));
            if (gpuStreamTestIndex >= 0)
            {
                if (gpuStreamTestIndex + 1 >= args.Length)
                {
                    throw new ArgumentException("--gpu-stream-smoke-test requires a project.json path.");
                }
                return await RunGpuStreamSmokeTestAsync(
                    args[gpuStreamTestIndex + 1], ReadDoubleOption(args, "--seconds", 10));
            }

            var decodeIndex = Array.FindIndex(args,
                value => value.Equals("--gpu-decode-smoke-test", StringComparison.OrdinalIgnoreCase));
            if (decodeIndex >= 0)
            {
                if (decodeIndex + 1 >= args.Length)
                {
                    throw new ArgumentException("--gpu-decode-smoke-test requires a media file path.");
                }
                using var decodeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var decoded = false;
                var summary = string.Empty;
                var reason = string.Empty;
                await Task.Run(() =>
                {
                    decoded = MediaFoundationDecodeCheck.TryDecode(args[decodeIndex + 1], out summary, out reason);
                }, decodeTimeout.Token);
                Console.WriteLine(decoded ? summary : "FAIL Media Foundation decode: " + reason);
                return decoded ? 0 : 1;
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

            Console.Error.WriteLine("Usage: CodexWallpaperSkin [--doctor [--json] | --restore | --auto-restore | --wait-and-restore | --self-test | --wgc-smoke-test | --gpu-encoder-smoke-test [--create-only] [--width N] [--height N] [--fps 30|60] [--seconds N] [--output PATH] | --we-capture-smoke-test <project.json> | --we-capture-soak-test <project.json> | --gpu-stream-smoke-test <project.json> [--seconds N]]");
            return 64;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// Live verification of the v0.4 GPU media path against a real Wallpaper
    /// Engine Scene: Windows Graphics Capture feeds the Media Foundation hardware
    /// encoder and every produced fragment is drained, so the test proves capture,
    /// encode, container assembly and disposal without needing the Codex page.
    /// The renderer half is covered by scripts/runtime-smoke-test.mjs.
    /// </summary>
    private static async Task<int> RunGpuStreamSmokeTestAsync(string projectPath, double seconds)
    {
        if (seconds is < 2 or > 120)
        {
            throw new ArgumentException("--seconds must be between 2 and 120 for the GPU stream smoke test.");
        }
        var wallpaper = WallpaperCatalog.ParseProject(projectPath);
        if (!wallpaper.IsWallpaperEngineScene)
        {
            throw new InvalidDataException("The GPU stream smoke test accepts Wallpaper Engine Scene projects only.");
        }

        var settings = new WallpaperSettings { Muted = true, SceneFrameRate = 60, SceneResolutionScale = .75 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 60));
        await using var session = await WallpaperEngineCaptureSession.StartAsync(
            wallpaper, settings, 1600, 1000, timeout.Token, useGpuMediaPath: true);
        if (!session.UsesGpuMediaPath)
        {
            throw new InvalidOperationException(
                "The GPU media path was not selected: "
                + (session.GpuStartFailureReason ?? "no reason was reported"));
        }
        if (session.Status != GpuStreamStatus.GpuDynamic60)
        {
            throw new InvalidOperationException(
                $"The GPU media path reported '{session.StatusLabel}' instead of the 60 FPS target.");
        }

        var batches = 0;
        var fragments = 0;
        var bytes = 0L;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        session.StartStreamingGpu(
            (batch, _) =>
            {
                batches++;
                fragments += batch.Count;
                bytes += batch.ByteCount;
                return Task.FromResult("presented");
            },
            _ => Task.FromResult<CapturedPointer?>(null));
        while (DateTimeOffset.UtcNow < deadline && session.IsRunning)
        {
            await Task.Delay(100, CancellationToken.None);
        }

        var snapshot = session.GpuDiagnostics;
        var status = session.StatusLabel;
        var codec = session.GpuCodec ?? "unknown";
        var failure = session.GpuFailureReason;
        await session.DisposeAsync();
        if (session.IsRenderWindowAlive)
        {
            throw new InvalidOperationException("The Wallpaper Engine render window remained open after capture disposal.");
        }
        if (failure is not null)
        {
            throw new InvalidOperationException("The GPU media path failed during the run: " + failure);
        }
        var minimumBatches = (int)(seconds * 10);
        if (batches < minimumBatches || snapshot is null || snapshot.EncodedFrames < 30)
        {
            throw new TimeoutException(
                $"The GPU media path produced too little media: {batches} batches (expected at least {minimumBatches}), "
                + $"{snapshot?.EncodedFrames ?? 0} encoded frames, {snapshot?.PresentedFragments ?? 0} acknowledged fragments.");
        }
        Console.WriteLine("  " + snapshot.Describe());
        Console.WriteLine(
            $"PASS GPU media path ({status}; {codec}; {batches} transport batches, {fragments} fragments, {bytes} bytes, "
            + $"encoder={snapshot.EncoderMode}, capture={snapshot.CaptureWidth}x{snapshot.CaptureHeight}, "
            + $"private window released)");
        return 0;
    }

    private static int ReadOption(string[] args, string name, int fallback) =>
        int.TryParse(ReadStringOption(args, name), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double ReadDoubleOption(string[] args, string name, double fallback) =>
        double.TryParse(ReadStringOption(args, name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string? ReadStringOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
