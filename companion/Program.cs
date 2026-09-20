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

            if (args.Contains("--measure", StringComparer.OrdinalIgnoreCase))
            {
                using var measurementTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                return await MeasurementCommand.RunAsync(args, measurementTimeout.Token);
            }

            Console.Error.WriteLine("Usage: CodexWallpaperSkin [--doctor [--json] | --restore | --auto-restore | --wait-and-restore | --self-test | --measure --list | --measure <workshop-id> [--seconds 60]]");
            return 64;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
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
