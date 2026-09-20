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
        double[] latencies = [];
        var applied = false;
        try
        {
            await injection.ConnectAsync(state.CdpBaseUrl, cancellationToken);
            var applyResult = await injection.ApplyAsync(wallpaper, state.Settings, cancellationToken: cancellationToken);
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
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
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
                    await injection.CleanupAsync(CancellationToken.None);
                    Console.WriteLine("Restore: the injected layer was removed and verified.");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("Restore failed: " + exception.Message);
                }
            }
        }

        PrintSummary(wallpaper, metrics, latencies, companion);
        return 0;
    }

    private static void PrintSummary(
        WallpaperEntry wallpaper,
        IReadOnlyList<CaptureHealth> metrics,
        IReadOnlyList<double> latencies,
        Process companion)
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
            framesPublishedPerSecondAverage = rates.Count == 0 ? 0 : Math.Round(rates.Average(), 2),
            framesPublishedPerSecondMinimum = rates.Count == 0 ? 0 : rates.Min(),
            inputLatencySamples = latencies.Count,
            inputLatencyMedianMs = latencies.Count == 0 ? 0 : Math.Round(Median(latencies), 2),
            inputLatencyMinMs = latencies.Count == 0 ? 0 : Math.Round(latencies.Min(), 2),
            inputLatencyMaxMs = latencies.Count == 0 ? 0 : Math.Round(latencies.Max(), 2),
            companionWorkingSetMiB = Math.Round(companion.WorkingSet64 / 1048576d, 1),
            note = "Input latency is injection-to-observation through the companion input channel. "
                + "Rejected frames are frames the quality gate refused to present."
        };
        Console.WriteLine();
        Console.WriteLine("=== measurement summary (JSON) ===");
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
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
