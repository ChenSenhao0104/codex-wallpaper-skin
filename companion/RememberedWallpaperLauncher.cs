using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace CodexWallpaperSkin;

/// <summary>
/// Entry point used by the optional desktop shortcut. It starts Codex with
/// the startup-only local wallpaper channel before restoring the saved item.
/// </summary>
public static class RememberedWallpaperLauncher
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var state = StateStore.Load();
        var wallpaper = AutoRestoreService.ResolveLastWallpaper(state);
        if (wallpaper is null)
        {
            var openController = MessageBox.Show(
                "No remembered wallpaper is available yet. Open Codex Wallpaper Skin and apply one first?",
                "Codex wallpaper launcher",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.Yes);
            if (openController == MessageBoxResult.Yes) StartInteractiveController();
            return 2;
        }

        // If a wallpaper-aware Codex is already alive, simply foreground it.
        // The existing GUI/worker remains the sole stream owner.
        if (!CdpEndpoint.IsAvailableForActivation(state.CdpBaseUrl))
        {
            var running = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
            if (running.Count > 0)
            {
                var restart = MessageBox.Show(
                    "Codex is already open from an entry that did not include its wallpaper channel.\n\n"
                    + "Codex cannot add this startup-only channel after it is already running. "
                    + "This launcher can request a normal shutdown and reopen it with the remembered wallpaper. "
                    + "No process will be force-terminated. Save or pause active work first.\n\n"
                    + "Restart Codex now?",
                    "Restore remembered Codex wallpaper",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (restart != MessageBoxResult.Yes) return 0;

                if (!DeferredRestoreLauncher.RequestStopAndWait(TimeSpan.FromSeconds(8)))
                {
                    MessageBox.Show(
                        "An older background wallpaper worker is still stopping, so Codex was left untouched. "
                        + "Wait a few seconds and open this shortcut again.",
                        "Codex wallpaper launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return 3;
                }
                await CodexRestartService.RequestNormalCloseAsync(TimeSpan.FromSeconds(45), cancellationToken);
            }
        }
        else
        {
            var aumid = string.IsNullOrWhiteSpace(state.Aumid) ? AppActivation.OfficialAumid : state.Aumid;
            AppActivation.ActivateWithCdp(aumid!, state.CdpBaseUrl);
            return 0;
        }

        return await DeferredRestoreLauncher.RunAsync(cancellationToken);
    }

    private static void StartInteractiveController()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The controller executable path could not be determined.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(processPath) ?? string.Empty
        };
        if (Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("The controller assembly path could not be determined.");
            startInfo.ArgumentList.Add(assemblyPath);
        }
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The controller could not be opened.");
    }
}
