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

    public static bool RequestStopAndWait(TimeSpan timeout)
    {
        RequestStop();
        return WaitForWorkerExit(timeout);
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
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopRegistration = ThreadPool.RegisterWaitForSingleObject(
            stopEvent,
            static (state, timedOut) =>
            {
                if (!timedOut) ((CancellationTokenSource)state!).Cancel();
            },
            workerCancellation,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
        var workerToken = workerCancellation.Token;
        using var mutex = new Mutex(false, WorkerMutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                var waitResult = WaitHandle.WaitAny([mutex, stopEvent], TimeSpan.FromSeconds(15));
                ownsMutex = waitResult == 0;
            }
            catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
            {
                ownsMutex = true;
            }
            if (!ownsMutex) return 0;

            while (!workerToken.IsCancellationRequested && !stopEvent.WaitOne(0))
            {
                var state = StateStore.Load();
                if (AutoRestoreService.ResolveLastWallpaper(state) is null) return 0;

                var running = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
                if (running.Count > 0 && !CdpEndpoint.IsAvailableForActivation(state.CdpBaseUrl))
                {
                    if (await DelayOrStopAsync(stopEvent, TimeSpan.FromSeconds(2), workerToken)) return 0;
                    continue;
                }

                await using var injection = new CdpInjectionService();
                try
                {
                    var restored = await AutoRestoreService.RestoreAsync(
                        state, injection, activateIfNeeded: true, cancellationToken: workerToken);
                    StateStore.Save(state);
                    Console.WriteLine($"Restored {restored.Wallpaper.DisplayTitle} ({restored.ApplyResult.Mode}).");
                    if (injection.HasActiveCapture)
                    {
                        var completion = injection.ActiveCaptureCompletion;
                        while (!completion.IsCompleted && !stopEvent.WaitOne(0))
                        {
                            await Task.WhenAny(completion, Task.Delay(250, workerToken));
                        }
                        if (stopEvent.WaitOne(0)) return 0;
                        await completion.WaitAsync(workerToken);
                        // Keep the last confirmed browser frame while a private
                        // Wallpaper Engine renderer is being recovered. Do not
                        // reopen Codex after the user intentionally closes it.
                        if (CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count == 0) return 0;
                        for (var recoveryAttempt = 1; recoveryAttempt <= 2; recoveryAttempt++)
                        {
                            if (await DelayOrStopAsync(
                                    stopEvent,
                                    recoveryAttempt == 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3),
                                    workerToken)) return 0;
                            if (CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count == 0) return 0;
                            try
                            {
                                restored = await AutoRestoreService.RestoreAsync(
                                    state, injection, activateIfNeeded: false, cancellationToken: workerToken);
                                StateStore.Save(state);
                                completion = injection.ActiveCaptureCompletion;
                                while (!completion.IsCompleted && !stopEvent.WaitOne(0))
                                {
                                    await Task.WhenAny(completion, Task.Delay(250, workerToken));
                                }
                                if (stopEvent.WaitOne(0)) return 0;
                                await completion.WaitAsync(workerToken);
                                if (CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count == 0) return 0;
                            }
                            catch when (recoveryAttempt < 2)
                            {
                                // One final bounded attempt remains.
                            }
                            catch
                            {
                                return 0;
                            }
                        }
                    }
                    return 0;
                }
                catch (CodexAlreadyRunningWithoutCdpException)
                {
                    if (await DelayOrStopAsync(stopEvent, TimeSpan.FromSeconds(2), workerToken)) return 0;
                }
                catch (TimeoutException) when (CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count > 0)
                {
                    if (await DelayOrStopAsync(stopEvent, TimeSpan.FromSeconds(2), workerToken)) return 0;
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (stopEvent.WaitOne(0) && !cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            stopRegistration.Unregister(null);
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
