using System.Reflection;
using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

internal static class LegacyDesktopLauncherCleanup
{
    internal const string LegacyArgument = "--launch-remembered-wallpaper";
    private const string LegacyFileName = "Codex with remembered wallpaper.lnk";

    public static void TryRemove()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            LegacyFileName);
        if (!File.Exists(path)) return;

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType is null) return;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return;
            shortcut = shell.GetType().InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, binder: null, shell, [path]);
            if (shortcut is null) return;
            var target = shortcut.GetType().InvokeMember(
                "TargetPath", BindingFlags.GetProperty, binder: null, shortcut, null) as string;
            var arguments = shortcut.GetType().InvokeMember(
                "Arguments", BindingFlags.GetProperty, binder: null, shortcut, null) as string;
            if (string.Equals(Path.GetFileName(target), "CodexWallpaperSkin.exe", StringComparison.OrdinalIgnoreCase)
                && arguments?.Trim().Equals(LegacyArgument, StringComparison.OrdinalIgnoreCase) == true)
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale shortcut is harmless. Never block normal startup because
            // the Desktop folder or Windows Script Host is unavailable.
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
