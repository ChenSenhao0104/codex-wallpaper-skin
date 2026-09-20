using System.Diagnostics;

namespace CodexWallpaperSkin;

/// <summary>
/// Terminates a process this companion started. It exists so Restore can release
/// an owned capture resource without ever touching a process the user was already
/// running — Wallpaper Engine in particular may be driving their desktop
/// wallpaper, so killing a reused instance would be destructive.
/// </summary>
public static class OwnedProcessTermination
{
    /// <summary>
    /// Terminates the process only when it still exists and still runs the
    /// expected image, so a recycled process id can never be hit by mistake.
    /// </summary>
    public static bool TryTerminate(
        int processId,
        string expectedImageName,
        TimeSpan? timeout = null)
    {
        if (processId <= 0 || string.IsNullOrWhiteSpace(expectedImageName))
        {
            return false;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // Already gone: nothing to release.
            return true;
        }
        catch
        {
            return false;
        }

        using (process)
        {
            try
            {
                var actualName = Path.GetFileNameWithoutExtension(process.ProcessName);
                if (!actualName.Equals(expectedImageName, StringComparison.OrdinalIgnoreCase))
                {
                    // The id was reused by an unrelated process; never kill it.
                    return false;
                }
                process.Kill(entireProcessTree: false);
                return process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
