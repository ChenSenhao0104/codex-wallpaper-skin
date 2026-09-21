using System.Reflection;
using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

public enum DesktopLauncherStatus
{
    Missing,
    CurrentExecutable,
    StaleExecutable
}

/// <summary>
/// Owns only the shortcut created by this application. The official Codex
/// shortcut is deliberately left untouched so uninstalling or moving this
/// portable build cannot break the user's normal Codex installation.
/// </summary>
public static class DesktopCodexLauncher
{
    internal const string FileName = "Codex with remembered wallpaper.lnk";
    internal const string LaunchArgument = "--launch-remembered-wallpaper";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        FileName);

    public static DesktopLauncherStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(ShortcutPath))
        {
            return DesktopLauncherStatus.Missing;
        }

        try
        {
            var (targetPath, arguments) = ReadShortcut(ShortcutPath);
            return string.Equals(targetPath, GetExecutablePath(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(arguments?.Trim(), LaunchArgument, StringComparison.OrdinalIgnoreCase)
                ? DesktopLauncherStatus.CurrentExecutable
                : DesktopLauncherStatus.StaleExecutable;
        }
        catch
        {
            return DesktopLauncherStatus.StaleExecutable;
        }
    }

    public static void InstallOrUpdate()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The desktop launcher is available only on Windows.");
        }

        var executable = GetExecutablePath();
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)
            ?? throw new InvalidOperationException("The Desktop folder is unavailable."));

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, ShortcutPath)
                ?? throw new InvalidOperationException("Windows did not create a shortcut object.");
            Set(shortcut, "TargetPath", executable);
            Set(shortcut, "Arguments", LaunchArgument);
            Set(shortcut, "WorkingDirectory", Path.GetDirectoryName(executable) ?? string.Empty);
            Set(shortcut, "Description", "Open Codex and restore the last remembered wallpaper");
            Set(shortcut, "IconLocation", executable + ",0");
            _ = Invoke(shortcut, "Save", BindingFlags.InvokeMethod);
        }
        finally
        {
            Release(shortcut);
            Release(shell);
        }
    }

    public static void Remove()
    {
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
    }

    internal static bool IsExpectedLaunchArgument(string? value) =>
        string.Equals(value?.Trim(), LaunchArgument, StringComparison.OrdinalIgnoreCase);

    private static (string? TargetPath, string? Arguments) ReadShortcut(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, path)
                ?? throw new InvalidOperationException("Windows did not open the shortcut object.");
            return (
                Get(shortcut, "TargetPath") as string,
                Get(shortcut, "Arguments") as string);
        }
        finally
        {
            Release(shortcut);
            Release(shell);
        }
    }

    private static string GetExecutablePath() =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("The controller executable path could not be determined.");

    private static object CreateShell()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false)
            ?? throw new InvalidOperationException("Windows Script Host shortcut support is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Windows Script Host could not be started.");
    }

    private static object? Invoke(object target, string member, BindingFlags flags, params object?[]? arguments) =>
        target.GetType().InvokeMember(member, flags, binder: null, target, arguments);

    private static object? Get(object target, string member) =>
        Invoke(target, member, BindingFlags.GetProperty);

    private static void Set(object target, string member, object? value) =>
        _ = Invoke(target, member, BindingFlags.SetProperty, value);

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
