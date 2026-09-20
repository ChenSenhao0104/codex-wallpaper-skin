using Microsoft.Win32;

namespace CodexWallpaperSkin;

public enum StartupRegistrationStatus
{
    Disabled,
    CurrentExecutable,
    StaleExecutable
}

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexWallpaperSkin.AutoRestore";

    public static bool IsEnabled() => GetStatus() == StartupRegistrationStatus.CurrentExecutable;

    public static StartupRegistrationStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return StartupRegistrationStatus.Disabled;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var saved = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(saved)) return StartupRegistrationStatus.Disabled;
        return string.Equals(saved, BuildCommand(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(saved, BuildLegacyCommand(), StringComparison.OrdinalIgnoreCase)
            ? StartupRegistrationStatus.CurrentExecutable
            : StartupRegistrationStatus.StaleExecutable;
    }

    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows startup registration is available only on Windows.");
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The current-user Windows startup registry key is unavailable.");
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static string BuildCommand()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("The controller executable path could not be determined.");
        }
        return $"\"{executable}\" --wait-and-restore";
    }

    private static string BuildLegacyCommand()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The controller executable path could not be determined.");
        return $"\"{executable}\" --auto-restore";
    }
}
