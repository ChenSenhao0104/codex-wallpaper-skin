using System.Diagnostics;

namespace CodexWallpaperSkin;

public sealed record CodexCloseResult(int ProcessCount, int CloseRequests);

public static class CodexRestartService
{
    public static async Task<CodexCloseResult> RequestNormalCloseAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var processIds = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
        if (processIds.Count == 0)
        {
            return new CodexCloseResult(0, 0);
        }

        var closeRequests = 0;
        foreach (var processId in processIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                {
                    closeRequests++;
                }
            }
            catch (ArgumentException)
            {
                // The process exited between the verified enumeration and close request.
            }
            catch (InvalidOperationException)
            {
                // The process exited between the verified enumeration and close request.
            }
        }

        if (closeRequests == 0 && CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count > 0)
        {
            throw new InvalidOperationException(
                "Codex is running, but Windows did not expose a normal closable app window. "
                + "Your wallpaper remains queued; close Codex normally when convenient and it will apply automatically.");
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count == 0)
            {
                return new CodexCloseResult(processIds.Count, closeRequests);
            }
            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException(
            "Codex did not finish its normal shutdown within the safety timeout. It was not force-terminated. "
            + "Your wallpaper remains queued and will apply after you close Codex normally.");
    }
}
