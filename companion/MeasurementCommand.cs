using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexWallpaperSkin;

/// <summary>
/// Local measurement harness for the evidence the Issue requires: capture
/// resolution, published/rejected frame counts, achieved frame rate, input
/// channel latency and process resources. It is read-only unless a wallpaper id
/// is supplied, and it always restores the original background afterwards.
/// </summary>
internal static class MeasurementCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var state = StateStore.Load();
        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            return ListCandidates(state);
        }

        var identifier = args
            .SkipWhile(argument => argument.StartsWith("--", StringComparison.Ordinal))
            .FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(identifier))
        {
            Console.Error.WriteLine("Usage: --measure --list | --measure <workshop-id> [--seconds 60]");
            return 64;
        }
        var seconds = ReadSeconds(args);
        return await MeasureAsync(
            state, identifier, seconds, ReadSaveFramePath(args), ReadPointerProbe(args), cancellationToken);
    }

    private static bool ReadPointerProbe(string[] args) =>
        args.Contains("--pointer-probe", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads --save-frame's path. The dump is written outside the repository by
    /// the caller and is only ever inspected locally.
    /// </summary>
    private static string? ReadSaveFramePath(string[] args)
    {
        var index = Array.FindIndex(args, argument => argument.Equals("--save-frame", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }
        var path = args[index + 1];
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    private static int ReadSeconds(string[] args)
    {
        var index = Array.FindIndex(args, argument => argument.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed))
        {
            return Math.Clamp(parsed, 5, 600);
        }
        return 60;
    }

    /// <summary>Lists local Scene projects and whether they reach the native backend.</summary>
    private static int ListCandidates(AppState state)
    {
        var roots = WallpaperCatalog.DiscoverWorkshopRoots().ToList();
        if (!string.IsNullOrWhiteSpace(state.WallpaperEngineRoot) && Directory.Exists(state.WallpaperEngineRoot))
        {
            roots.Insert(0, state.WallpaperEngineRoot);
        }
        roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"Workshop roots discovered: {roots.Count}");
        var found = WallpaperCatalog.ScanWorkshopRoots(roots);
        var scenes = found.Where(item => item.Kind == WallpaperKind.Scene).OrderBy(item => item.Title).ToList();
        Console.WriteLine($"Projects scanned: {found.Count}; Scene projects: {scenes.Count}");
        Console.WriteLine();
        Console.WriteLine("workshop-id | backend | engine | package | title");
        foreach (var scene in scenes)
        {
            var projectPath = scene.ProjectPath ?? string.Empty;
            var engine = WallpaperEngineLocator.IsEngineAvailable(projectPath);
            var packagePath = WallpaperCatalog.ResolveScenePackagePath(scene);
            Console.WriteLine(string.Join(" | ",
                WorkshopIdOf(scene),
                scene.Support,
                engine ? "found" : "missing",
                DescribePackage(packagePath),
                scene.Title));
        }
        Console.WriteLine();
        Console.WriteLine("Native-eligible scenes can be measured with: --measure <workshop-id> --seconds 60");
        return 0;
    }

    private static async Task<int> MeasureAsync(
        AppState state,
        string identifier,
        int seconds,
        string? savedFramePathParameter,
        bool pointerProbe,
        CancellationToken cancellationToken)
    {
        var roots = WallpaperCatalog.DiscoverWorkshopRoots().ToList();
        if (!string.IsNullOrWhiteSpace(state.WallpaperEngineRoot) && Directory.Exists(state.WallpaperEngineRoot))
        {
            roots.Insert(0, state.WallpaperEngineRoot);
        }
        var scenes = WallpaperCatalog.ScanWorkshopRoots(roots.Distinct(StringComparer.OrdinalIgnoreCase))
            .Where(item => item.Kind == WallpaperKind.Scene)
            .ToList();
        var match = scenes.FirstOrDefault(item =>
                        WorkshopIdOf(item).Equals(identifier, StringComparison.OrdinalIgnoreCase))
                    ?? scenes.FirstOrDefault(item =>
                        item.Title.Contains(identifier, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Console.Error.WriteLine($"No local Scene project matched '{identifier}'. Run --measure --list.");
            return 2;
        }
        var wallpaper = WallpaperCatalog.ParseProject(match.ProjectPath!);
        Console.WriteLine($"Measuring {WorkshopIdOf(wallpaper)} ({wallpaper.Title})");
        Console.WriteLine($"Backend selected at discovery: {wallpaper.Support}");
        Console.WriteLine($"Capture path: {CaptureBackends.Describe(CaptureBackends.Availability)}");
        Console.WriteLine($"Duration: {seconds}s");
        Console.WriteLine();

        await using var injection = new CdpInjectionService();
        var companion = Process.GetCurrentProcess();
        var metrics = new List<CaptureHealth>();
        CaptureTransportMetrics? transport = null;
        string? ownedWindowName = null;
        var openOwnedWindowsAfterRestore = 0;
        var ownedWindowPresentBeforeRestore = false;
        double[] latencies = [];
        var applied = false;
        try
        {
            await injection.ConnectAsync(state.CdpBaseUrl, cancellationToken);
            var settings = MeasurementSettings();
            Console.WriteLine("Settings: fit=cover, opacity=1, overlay=0, blur=0, brightness/contrast/saturation=1, "
                + "sceneFps=15, sceneScale=1, pauseWhenHidden=false");
            var applyResult = await injection.ApplyAsync(wallpaper, settings, cancellationToken: cancellationToken);
            applied = true;
            Console.WriteLine($"Applied: mode={applyResult.Mode}");
            if (!string.IsNullOrWhiteSpace(applyResult.Warning))
            {
                Console.WriteLine($"Warning: {applyResult.Warning}");
            }
            Console.WriteLine($"Backend status: {BackendStatuses.Describe(injection.BackendStatus)}");
            Console.WriteLine();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
            var lastPublished = 0;
            var lastSampleAt = DateTimeOffset.UtcNow;
            var rates = new List<double>();
            var resources = new List<ResourceSample>();
            var tick = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                // Resource sampling walks every Codex process, so it runs less
                // often than the frame counter.
                if (tick++ % 5 == 0)
                {
                    resources.Add(SampleResources(companion));
                }
                var health = injection.CaptureHealthSnapshot;
                if (health is null)
                {
                    Console.WriteLine("capture stream is no longer active");
                    break;
                }
                metrics.Add(health);
                var now = DateTimeOffset.UtcNow;
                var elapsed = (now - lastSampleAt).TotalSeconds;
                var delta = health.PublishedFrames - lastPublished;
                if (elapsed > 0 && delta >= 0)
                {
                    rates.Add(delta / elapsed);
                }
                lastPublished = health.PublishedFrames;
                lastSampleAt = now;
            }

            if (injection.HasActiveCapture)
            {
                latencies = (await injection.MeasureInputLatencyAsync(12, cancellationToken)).ToArray();
            }
            transport = injection.TransportMetrics;
            // Gate 8 needs the window identity before Restore clears the session.
            ownedWindowName = injection.OwnedCaptureWindowName;
            if (pointerProbe && injection.HasActiveCapture)
            {
                await RunPointerProbeAsync(injection, savedFramePathParameter, cancellationToken);
            }
            var savedFramePath = savedFramePathParameter;
            if (savedFramePath is not null)
            {
                var frame = injection.LatestCaptureFrame;
                if (frame is null || frame.Length == 0)
                {
                    Console.WriteLine("Frame dump: no accepted frame was captured, so nothing was written.");
                }
                else
                {
                    await File.WriteAllBytesAsync(savedFramePath, frame, cancellationToken);
                    Console.WriteLine($"Frame dump: wrote {frame.Length} B to {savedFramePath} for local inspection "
                        + "(never committed).");
                }
            }
            PrintSummary(wallpaper, metrics, latencies, companion, resources, transport);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Measurement error: " + exception.Message);
            return 1;
        }
        finally
        {
            if (applied)
            {
                try
                {
                    // Restore every Codex app page, not just the attached one: a
                    // measurement must leave no artifacts anywhere.
                    var cleaned = await injection.CleanupAllAsync(state.CdpBaseUrl, CancellationToken.None);
                    Console.WriteLine($"Restore: removed the injected layer from {cleaned} page(s).");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("Restore failed: " + exception.Message);
                }
            }

            // Gate 8: the render window this run owned must no longer exist on the
            // desktop, checked against the desktop rather than the session object.
            ownedWindowPresentBeforeRestore = RenderWindowProbe.IsWindowPresent(ownedWindowName ?? string.Empty);
            openOwnedWindowsAfterRestore = RenderWindowProbe.CountOpenWindows(RenderWindowProbe.WindowTitlePrefix);
            Console.WriteLine(ownedWindowName is null
                ? "Gate 8: no owned render window was created by this run."
                : $"Gate 8: this run's window present after restore: {ownedWindowPresentBeforeRestore}; "
                    + $"open private render windows now: {openOwnedWindowsAfterRestore}.");
            if (openOwnedWindowsAfterRestore > 0)
            {
                Console.WriteLine("Gate 8: FAIL — a private render window survived restore.");
            }
        }

        PrintRestoreVerdict(ownedWindowName, ownedWindowPresentBeforeRestore, openOwnedWindowsAfterRestore);
        return 0;
    }

    private static void PrintRestoreVerdict(
        string? ownedWindowName,
        bool presentBeforeRestore,
        int openAfterRestore)
    {
        if (ownedWindowName is null)
        {
            Console.WriteLine("Owned capture window released: not applicable (no native capture session ran).");
            return;
        }
        Console.WriteLine(openAfterRestore == 0 && !presentBeforeRestore
            ? "Owned capture window released: yes (the owned window was already gone before the check)."
            : openAfterRestore == 0
                ? "Owned capture window released: yes."
                : $"Owned capture window released: NO ({openAfterRestore} window(s) still open).");
    }

    /// <summary>Working set and CPU for the companion, Wallpaper Engine and Codex.</summary>
    private sealed record ResourceSample(
        double CompanionMiB,
        double EngineMiB,
        double EngineCpuSeconds,
        double CodexMiB,
        double CodexCpuSeconds);

    private static ResourceSample SampleResources(Process companion)
    {
        companion.Refresh();
        var (engineMiB, engineCpu) = SumProcesses(["wallpaper64", "wallpaper32"]);
        var codexIds = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
        var (codexMiB, codexCpu) = SumProcessesById(codexIds);
        return new ResourceSample(
            companion.WorkingSet64 / 1048576d,
            engineMiB,
            engineCpu,
            codexMiB,
            codexCpu);
    }

    private static (double WorkingSetMiB, double CpuSeconds) SumProcesses(IEnumerable<string> names)
    {
        double workingSet = 0;
        double cpu = 0;
        foreach (var name in names)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        workingSet += process.WorkingSet64 / 1048576d;
                        cpu += process.TotalProcessorTime.TotalSeconds;
                    }
                    catch
                    {
                        // A process can exit while it is inspected.
                    }
                }
            }
        }
        return (workingSet, cpu);
    }

    private static (double WorkingSetMiB, double CpuSeconds) SumProcessesById(IEnumerable<int> processIds)
    {
        double workingSet = 0;
        double cpu = 0;
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                workingSet += process.WorkingSet64 / 1048576d;
                cpu += process.TotalProcessorTime.TotalSeconds;
            }
            catch
            {
                // A process can exit while it is inspected.
            }
        }
        return (workingSet, cpu);
    }

    private static void PrintSummary(
        WallpaperEntry wallpaper,
        IReadOnlyList<CaptureHealth> metrics,
        IReadOnlyList<double> latencies,
        Process companion,
        IReadOnlyList<ResourceSample> resources,
        CaptureTransportMetrics? transport)
    {
        var last = metrics.Count > 0 ? metrics[^1] : null;
        var rates = new List<double>();
        for (var index = 1; index < metrics.Count; index++)
        {
            rates.Add(Math.Max(0, metrics[index].PublishedFrames - metrics[index - 1].PublishedFrames));
        }
        companion.Refresh();

        var summary = new
        {
            wallpaper = WorkshopIdOf(wallpaper),
            title = wallpaper.Title,
            backend = BackendStatuses.Describe(BackendStatuses.FromApplyMode(
                wallpaper.IsNativeScene ? "wallpaper-engine-capture" : "live-scene")),
            captureBackend = CaptureBackends.BackendName(CaptureBackends.ImplementedCaptureBackend),
            samples = metrics.Count,
            targetFrameRate = last?.TargetFrameRate ?? 0,
            effectiveFrameRate = last is null ? 0 : Math.Round(last.EffectiveFrameRate, 2),
            publishedFrames = last?.PublishedFrames ?? 0,
            rejectedFrames = last?.RejectedFrames ?? 0,
            skippedUnchangedFrames = last?.SkippedUnchangedFrames ?? 0,
            framesPublishedPerSecondAverage = rates.Count == 0 ? 0 : Math.Round(rates.Average(), 2),
            framesPublishedPerSecondMinimum = rates.Count == 0 ? 0 : rates.Min(),
            averageFrameBytes = transport is null ? 0 : Math.Round(transport.AverageFrameBytes, 1),
            maximumFrameBytes = transport?.MaximumFrameBytes ?? 0,
            transportedBytes = transport?.PublishedBytes ?? 0,
            bytesPerSecond = transport is null || metrics.Count == 0
                ? 0
                : Math.Round(transport.PublishedBytes / Math.Max(1d, metrics.Count), 1),
            averageFrameTransferMs = transport is null ? 0 : Math.Round(transport.AveragePublishMilliseconds, 2),
            inputLatencySamples = latencies.Count,
            inputLatencyMedianMs = latencies.Count == 0 ? 0 : Math.Round(Median(latencies), 2),
            inputLatencyMinMs = latencies.Count == 0 ? 0 : Math.Round(latencies.Min(), 2),
            inputLatencyMaxMs = latencies.Count == 0 ? 0 : Math.Round(latencies.Max(), 2),
            companionWorkingSetMiBMaB = Range(resources.Select(sample => sample.CompanionMiB)),
            engineWorkingSetMiBMaB = Range(resources.Select(sample => sample.EngineMiB)),
            engineCpuSeconds = CpuDelta(resources.Select(sample => sample.EngineCpuSeconds)),
            codexWorkingSetMiBMaB = Range(resources.Select(sample => sample.CodexMiB)),
            codexCpuSeconds = CpuDelta(resources.Select(sample => sample.CodexCpuSeconds)),
            note = "Input latency is injection-to-observation through the companion input channel. "
                + "Rejected frames are frames the quality gate refused to present; unchanged frames were "
                + "identical to the frame already on screen and were not re-transported. "
                + "Working-set and CPU deltas are sampled every 5s across the run."
        };
        Console.WriteLine();
        Console.WriteLine("=== measurement summary (JSON) ===");
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Maximum and average, or zeros when nothing was sampled.</summary>
    private static object Range(IEnumerable<double> values)
    {
        var list = values.Where(value => value > 0).ToArray();
        return list.Length == 0
            ? new { max = 0d, average = 0d }
            : new { max = Math.Round(list.Max(), 1), average = Math.Round(list.Average(), 1) };
    }

    /// <summary>CPU consumed across the run, from the first and last sample.</summary>
    private static double CpuDelta(IEnumerable<double> values)
    {
        var list = values.ToArray();
        return list.Length < 2 ? 0 : Math.Round(list[^1] - list[0], 2);
    }

    /// <summary>
    /// Neutral, reproducible visual settings so a measurement reflects the
    /// capture path rather than whatever the user last tuned. Hidden-page
    /// throttling is off so a covered or minimised window cannot distort the
    /// frame-rate number.
    /// </summary>
    private static WallpaperSettings MeasurementSettings() => new WallpaperSettings
    {
        Fit = WallpaperFit.Cover,
        FocusX = 50,
        FocusY = 50,
        Opacity = 1,
        BlackOverlay = 0,
        AutoPalette = true,
        PaletteStrength = 0.72,
        PanelOpacity = 0.72,
        TintInterfaceText = true,
        Blur = 0,
        Brightness = 1,
        Contrast = 1,
        Saturation = 1,
        PlaybackRate = 1,
        Muted = true,
        PauseWhenHidden = false,
        SceneFrameRate = 15,
        SceneResolutionScale = 1
    }.Normalize();

    /// <summary>
    /// Answers "does the scene react to the pointer?" with an A/B observation on
    /// the *rendered* output. Frames are sampled first with the pointer parked and
    /// then while it is swept across the scene, and the temporal variability of a
    /// water band is compared with a control band that should not be
    /// pointer-driven. A scene that ignores pointer input shows no difference
    /// between the two phases; a pointer-driven effect raises the swept phase in
    /// the water band only.
    /// </summary>
    private static async Task RunPointerProbeAsync(
        CdpInjectionService injection,
        string? savedFramePrefix,
        CancellationToken cancellationToken)
    {
        const int samplesPerPhase = 10;
        // Derive a stem so the two phase dumps sit next to the requested path
        // instead of stacking extensions.
        var stem = savedFramePrefix is null
            ? null
            : Path.Combine(
                Path.GetDirectoryName(savedFramePrefix) ?? ".",
                Path.GetFileNameWithoutExtension(savedFramePrefix));
        var quiet = await SampleBandsAsync(
            injection, PointerPhase.Quiet, samplesPerPhase, stem is null ? null : stem + ".quiet.jpg", cancellationToken);
        var pageHidden = await injection.IsPageHiddenAsync(cancellationToken);
        Console.WriteLine($"Pointer probe: the Codex page reports itself hidden: {pageHidden}. "
            + (pageHidden == true
                ? "Pointer forwarding is skipped while hidden, so this probe forces it to keep the measurement valid."
                : "Pointer forwarding is active."));
        injection.ForceInputWhileHidden(true);
        var swept = await SampleBandsAsync(
            injection, PointerPhase.MoveSweep, samplesPerPhase, stem is null ? null : stem + ".swept.jpg", cancellationToken);
        var dragged = await SampleBandsAsync(
            injection, PointerPhase.DragSweep, samplesPerPhase, stem is null ? null : stem + ".drag.jpg", cancellationToken);
        if (quiet is null || swept is null)
        {
            Console.WriteLine("Pointer probe: no frames were available, so the scene reaction could not be judged.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("=== pointer interaction probe ===");
        Console.WriteLine("phase         water pixel delta   control pixel delta   water luma sd");
        PrintPhase("quiet", quiet);
        PrintPhase("move sweep", swept);
        if (dragged is not null)
        {
            PrintPhase("drag sweep", dragged);
        }

        var quietWater = quiet["water"].MeanPixelDelta;
        var best = swept["water"].MeanPixelDelta;
        var bestLabel = "move sweep";
        if (dragged is not null && dragged["water"].MeanPixelDelta > best)
        {
            best = dragged["water"].MeanPixelDelta;
            bestLabel = "drag sweep";
        }
        var controlBest = Math.Max(swept["control"].MeanPixelDelta, dragged?["control"].MeanPixelDelta ?? 0);
        var gain = best - quietWater;
        var controlGain = controlBest - quiet["control"].MeanPixelDelta;
        var (forwarded, failed) = injection.InputMessageCounts;
        Console.WriteLine($"pointer messages posted to the render window: {forwarded} accepted, {failed} refused");
        Console.WriteLine($"best water pixel-delta gain: {gain:+0.000;-0.000;0.000} from '{bestLabel}' "
            + $"(quiet {quietWater:0.000} -> {best:0.000}); control gain {controlGain:+0.000;-0.000;0.000}");
        Console.WriteLine(gain > 0.25 && gain > controlGain * 2
            ? $"Verdict: the native render reacts to pointer input in the water band (via {bestLabel}), and not in the control band."
            : "Verdict: the native render showed no measurable pointer-driven reaction in the water band.");

        void PrintPhase(string label, Dictionary<string, BandStats> phase) =>
            Console.WriteLine($"{label,-13} {phase["water"].MeanPixelDelta,18:0.000} {phase["control"].MeanPixelDelta,21:0.000} "
                + $"{phase["water"].LumaStdDev,15:0.00}");
    }

    private enum PointerPhase
    {
        Quiet,
        MoveSweep,
        DragSweep
    }

    private sealed record BandStats(double MeanPixelDelta, double LumaStdDev);

    /// <summary>
    /// Samples the rendered output for one phase and returns per-band statistics:
    /// how much individual pixels change from frame to frame (which is what a
    /// ripple actually does) and how much the band's mean luminance varies. Band
    /// mean brightness alone is nearly blind to ripples, which is why the pixel
    /// delta is the primary signal here.
    /// </summary>
    private static async Task<Dictionary<string, BandStats>?> SampleBandsAsync(
        CdpInjectionService injection,
        PointerPhase phase,
        int sampleCount,
        string? savedFramePath,
        CancellationToken cancellationToken)
    {
        var waterLuma = new List<double>();
        var controlLuma = new List<double>();
        var waterDeltas = new List<double>();
        var controlDeltas = new List<double>();
        double[]? previousWater = null;
        double[]? previousControl = null;
        byte[]? last = null;
        var sweeping = phase != PointerPhase.Quiet;
        if (phase == PointerPhase.DragSweep)
        {
            // Press first: this scene's water responds to a drag, so a hover-only
            // sweep would test the wrong interaction.
            await injection.DispatchPointerAsync(0.5, 0.62, buttonDown: true, cancellationToken);
            await Task.Delay(120, cancellationToken);
        }
        try
        {
            for (var index = 0; index < sampleCount; index++)
            {
                if (sweeping)
                {
                    var angle = index / (double)sampleCount * Math.PI * 2;
                    await injection.DispatchPointerAsync(
                        0.5 + 0.32 * Math.Cos(angle),
                        0.62 + 0.22 * Math.Sin(angle),
                        buttonDown: null,
                        cancellationToken);
                }
                await Task.Delay(160, cancellationToken);
                var frame = injection.LatestCaptureFrame;
                if (frame is null || frame.Length == 0)
                {
                    continue;
                }
                last = frame;
                var bands = MeasureBands(frame);
                if (bands is null)
                {
                    continue;
                }
                var currentWater = bands.Value.Water;
                var currentControl = bands.Value.Control;
                if (previousWater is not null && previousControl is not null
                    && previousWater.Length == currentWater.Length)
                {
                    waterDeltas.Add(MeanAbsoluteDifference(previousWater, currentWater));
                    controlDeltas.Add(MeanAbsoluteDifference(previousControl, currentControl));
                }
                previousWater = currentWater;
                previousControl = currentControl;
                waterLuma.Add(currentWater.Average());
                controlLuma.Add(currentControl.Average());
            }
        }
        finally
        {
            if (phase == PointerPhase.DragSweep)
            {
                try
                {
                    await injection.DispatchPointerAsync(0.5, 0.62, buttonDown: false, CancellationToken.None);
                }
                catch
                {
                    // Releasing is best effort; capture teardown will follow anyway.
                }
            }
        }

        if (last is null || waterLuma.Count < 3)
        {
            return null;
        }
        if (savedFramePath is not null)
        {
            await File.WriteAllBytesAsync(savedFramePath, last, cancellationToken);
            Console.WriteLine($"Pointer probe: wrote {savedFramePath}");
        }
        return new Dictionary<string, BandStats>
        {
            ["water"] = BuildStats(waterLuma, waterDeltas),
            ["control"] = BuildStats(controlLuma, controlDeltas)
        };
    }

    private static double MeanAbsoluteDifference(double[] previous, double[] current)
    {
        var sum = 0d;
        for (var index = 0; index < current.Length; index++)
        {
            sum += Math.Abs(current[index] - previous[index]);
        }
        return current.Length == 0 ? 0 : sum / current.Length;
    }

    private static BandStats BuildStats(IReadOnlyList<double> values, IReadOnlyList<double> deltas)
    {
        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Count;
        return new BandStats(
            deltas.Count == 0 ? 0 : deltas.Average(),
            Math.Sqrt(variance));
    }

    /// <summary>
    /// Sampled luminance of two regions of a captured JPEG: the water band a
    /// Saki-like scene exposes to the pointer, and a wall band it does not.
    /// </summary>
    private static (double[] Water, double[] Control)? MeasureBands(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                frame, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var buffer = new byte[stride * height];
            converted.CopyPixels(buffer, stride, 0);
            return (
                SampleBandLuma(buffer, stride, width, height, 0.20, 0.80, 0.45, 0.95),
                SampleBandLuma(buffer, stride, width, height, 0.20, 0.80, 0.02, 0.16));
        }
        catch
        {
            return null;
        }
    }

    private static double[] SampleBandLuma(
        byte[] bgra, int stride, int width, int height,
        double x0, double x1, double y0, double y1)
    {
        var values = new List<double>();
        for (var y = (int)(height * y0); y < (int)(height * y1); y += 3)
        {
            var offset = y * stride;
            for (var x = (int)(width * x0); x < (int)(width * x1); x += 3)
            {
                var index = offset + x * 4;
                values.Add(0.114 * bgra[index] + 0.587 * bgra[index + 1] + 0.299 * bgra[index + 2]);
            }
        }
        return [.. values];
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2;
    }

    /// <summary>The Workshop folder name, or a stable fallback for local projects.</summary>
    private static string WorkshopIdOf(WallpaperEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ProjectPath))
        {
            return "unknown";
        }
        try
        {
            var directory = new FileInfo(Path.GetFullPath(entry.ProjectPath)).Directory;
            return directory?.Name ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Reads the package header directly: the safe parser deliberately refuses
    /// versions and sizes it cannot render, but the native path only needs the
    /// facts, so the measurement must not depend on parser acceptance.
    /// </summary>
    private static string DescribePackage(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return "no scene.pkg";
        }
        try
        {
            var file = new FileInfo(packagePath);
            var header = new byte[12];
            using var stream = File.OpenRead(packagePath);
            var read = stream.Read(header, 0, header.Length);
            var version = read >= 12
                ? Encoding.ASCII.GetString(header, 4, 8)
                : "unreadable";
            var sizeMiB = file.Length / 1048576d;
            return $"{version}, {sizeMiB:0.00} MiB";
        }
        catch (Exception exception)
        {
            return "unreadable (" + exception.GetType().Name + ")";
        }
    }
}
