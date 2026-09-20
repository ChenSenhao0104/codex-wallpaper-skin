using System.Diagnostics;
using System.Reflection;

namespace CodexWallpaperSkin;

public static class DeferredRestoreLauncher
{
    private const string WorkerArgument = "--wait-and-restore";
    // v2 deliberately does not wait on the legacy mutex. Older preview builds
    // did not understand the stop event and could hold their mutex forever
    // after an upgrade, preventing the corrected worker from taking over.
    private const string WorkerMutexName = @"Local\CodexWallpaperSkin.DeferredRestore.v2";
    private const string WorkerStopEventName = @"Local\CodexWallpaperSkin.DeferredRestore.v2.Stop";

    public static void RequestStop()
    {
        using var stopEvent = OpenStopEvent();
        stopEvent.Set();
    }

    public static void EnsureRunning()
    {
        // A previous GUI session may have handed capture to a worker. Revoke it
        // before starting the replacement so two versions cannot keep writing
        // frames and state to the same Codex page indefinitely.
        RequestStop();
        if (!WaitForWorkerExit(TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException(
                "A previous background wallpaper worker did not stop. Close the older Codex Wallpaper Skin build before retrying.");
        }
        using (var stopEvent = OpenStopEvent())
        {
            stopEvent.Reset();
        }
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
        using var stopEvent = OpenStopEvent();
        using var mutex = new Mutex(false, WorkerMutexName);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) return 0;

            while (!cancellationToken.IsCancellationRequested && !stopEvent.WaitOne(0))
            {
                var state = StateStore.Load();
                if (AutoRestoreService.ResolveLastWallpaper(state) is null) return 0;

                var running = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
                if (running.Count > 0 && !CdpEndpoint.IsAvailableForActivation(state.CdpBaseUrl))
                {
                    if (await DelayOrStopAsync(stopEvent, TimeSpan.FromSeconds(2), cancellationToken)) return 0;
                    continue;
                }

                await using var injection = new CdpInjectionService();
                try
                {
                    var restored = await AutoRestoreService.RestoreAsync(
                        state, injection, activateIfNeeded: true, cancellationToken: cancellationToken);
                    StateStore.Save(state);
                    Console.WriteLine($"Restored {restored.Wallpaper.DisplayTitle} ({restored.ApplyResult.Mode}).");
                    if (injection.HasActiveCapture)
                    {
                        var completion = injection.ActiveCaptureCompletion;
                        while (!completion.IsCompleted && !stopEvent.WaitOne(0))
                        {
                            await Task.WhenAny(completion, Task.Delay(250, cancellationToken));
                        }
                        if (stopEvent.WaitOne(0)) return 0;
                        await completion.WaitAsync(cancellationToken);
                    }
                    return 0;
                }
                catch (CodexAlreadyRunningWithoutCdpException)
                {
                    if (await DelayOrStopAsync(stopEvent, TimeSpan.FromSeconds(2), cancellationToken)) return 0;
                }
                catch (TimeoutException) when (CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count > 0)
                {
                    if (await DelayOrStopAsync(stopEvent, TimeSpan.FromSeconds(2), cancellationToken)) return 0;
                }
            }
            return 0;
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    private static EventWaitHandle OpenStopEvent() =>
        new(false, EventResetMode.ManualReset, WorkerStopEventName);

    private static bool WaitForWorkerExit(TimeSpan timeout)
    {
        using var mutex = new Mutex(false, WorkerMutexName);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            return ownsMutex;
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    private static async Task<bool> DelayOrStopAsync(
        EventWaitHandle stopEvent,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + delay;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopEvent.WaitOne(0)) return true;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250)
                ? remaining
                : TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        return stopEvent.WaitOne(0);
    }
}
