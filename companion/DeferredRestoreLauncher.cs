using System.Diagnostics;

namespace CodexWallpaperSkin;

public static class DeferredRestoreLauncher
{
    private const string WorkerArgument = "--wait-and-restore";

    public static void EnsureRunning()
    {
        var invocation = ControllerInvocation.ForCurrentProcess(WorkerArgument);
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (invocation.AssemblyPath is not null)
        {
            startInfo.ArgumentList.Add(invocation.AssemblyPath);
        }
        startInfo.ArgumentList.Add(invocation.Argument);
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
                    RecordQueueFailure(state, QueueFailureReason.CodexRunningWithoutCdp);
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
                    RecordQueueFailure(state, QueueFailureReason.CodexRunningWithoutCdp);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (TimeoutException)
                {
                    RecordQueueFailure(state, QueueFailureReason.CdpNotReady);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (Exception exception) when (
                    CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count > 0
                    || exception is InvalidOperationException or FileNotFoundException or IOException)
                {
                    // A background retry must never surface a modal error. Record
                    // the bounded reason so the controller window can explain the
                    // state and offer an explicit retry after the next restart.
                    RecordQueueFailure(state, QueueFailureReason.ActivationFailed);
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

    private static void RecordQueueFailure(AppState state, QueueFailureReason reason)
    {
        try
        {
            if (!WallpaperQueue.HasQueued(state))
            {
                var wallpaperId = state.PendingWallpaperId ?? state.LastAppliedWallpaperId;
                if (string.IsNullOrWhiteSpace(wallpaperId))
                {
                    return;
                }
                WallpaperQueue.Enqueue(state, wallpaperId, reason, DateTimeOffset.UtcNow);
            }
            else
            {
                WallpaperQueue.RecordFailure(state, reason, DateTimeOffset.UtcNow);
            }
            StateStore.Save(state);
        }
        catch
        {
            // The queue is best-effort background bookkeeping; a failed save must
            // not terminate the restore worker or interrupt Codex.
        }
    }
}
