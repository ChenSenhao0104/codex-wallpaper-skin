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
        return await MeasureAsync(state, identifier, seconds, cancellationToken);
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
