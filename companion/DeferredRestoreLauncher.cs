using System.Diagnostics;
using System.Reflection;

namespace CodexWallpaperSkin;

public static class DeferredRestoreLauncher
{
    private const string WorkerArgument = "--wait-and-restore";

    public static void EnsureRunning()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The controller executable path could not be determined.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidOperationException("The controller assembly path could not be determined.");
            }
            startInfo.ArgumentList.Add(assemblyPath);
        }
        startInfo.ArgumentList.Add(WorkerArgument);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The deferred wallpaper restore worker could not be started.");
    }

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var mutex = new Mutex(false, @"Local\CodexWallpaperSkin.DeferredRestore");
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) return 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var state = StateStore.Load();
                if (AutoRestoreService.ResolveLastWallpaper(state) is null) return 0;

                var running = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
                if (running.Count > 0 && !CdpEndpoint.IsAvailableForActivation(state.CdpBaseUrl))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                await using var injection = new CdpInjectionService();
                try
                {
                    var restored = await AutoRestoreService.RestoreAsync(
                        state, injection, activateIfNeeded: true, cancellationToken: cancellationToken);
                    StateStore.Save(state);
                    Console.WriteLine($"Restored {restored.Wallpaper.Title} ({restored.ApplyResult.Mode}).");
                    if (injection.HasActiveCapture)
                    {
                        await injection.ActiveCaptureCompletion.WaitAsync(cancellationToken);
                    }
                    return 0;
                }
                catch (CodexAlreadyRunningWithoutCdpException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (TimeoutException) when (CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
            return 0;
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }
}
