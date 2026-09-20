using Microsoft.Win32;

namespace CodexWallpaperSkin;

public enum StartupRegistrationStatus
{
    Disabled,
    Current,
    LegacyArgument,
    StaleExecutable,
    Unreadable,
    Unavailable
}

public sealed record StartupRegistrationState(StartupRegistrationStatus Status, ControllerInvocation? Registered)
{
    /// <summary>True when the saved entry exists but no longer matches this build.</summary>
    public bool NeedsRepair => Status is StartupRegistrationStatus.LegacyArgument or StartupRegistrationStatus.StaleExecutable;

    public bool IsEnabled => Status is not (StartupRegistrationStatus.Disabled or StartupRegistrationStatus.Unavailable);
}

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "CodexWallpaperSkin.AutoRestore";
    internal const string CurrentArgument = "--wait-and-restore";
    internal const string LegacyArgument = "--auto-restore";

    public static StartupRegistrationState GetState()
    {
        var expected = TryBuildCurrentInvocation();
        if (expected is null || !OperatingSystem.IsWindows())
        {
            return new StartupRegistrationState(StartupRegistrationStatus.Unavailable, null);
        }
        string? saved;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            saved = key?.GetValue(ValueName) as string;
        }
        catch
        {
            return new StartupRegistrationState(StartupRegistrationStatus.Unreadable, null);
        }
        return new StartupRegistrationState(Evaluate(saved, expected), ControllerInvocation.Parse(saved));
    }

    public static bool IsEnabled() => GetState() is { Status: not (StartupRegistrationStatus.Disabled or StartupRegistrationStatus.Unavailable) };

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

    /// <summary>
    /// Rewrites a stale or outdated entry so that it points at the executable
    /// that is running right now.
    /// </summary>
    public static StartupRegistrationState Repair()
    {
        SetEnabled(true);
        return GetState();
    }

    internal static string BuildCommand() => BuildCurrentInvocation().ToCommandLine();

    /// <summary>
    /// Pure decision function: compares the persisted command line with the
    /// invocation this process would need and reports the exact mismatch.
    /// </summary>
    internal static StartupRegistrationStatus Evaluate(string? saved, ControllerInvocation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (string.IsNullOrWhiteSpace(saved))
        {
            return StartupRegistrationStatus.Disabled;
        }
        var parsed = ControllerInvocation.Parse(saved);
        if (parsed is null)
        {
            return StartupRegistrationStatus.Unreadable;
        }
        if (parsed.Matches(expected))
        {
            return StartupRegistrationStatus.Current;
        }
        if (parsed.TargetsSameBinary(expected))
        {
            // Same binary, different switch: an entry written by an older version.
            return parsed.Argument.Equals(LegacyArgument, StringComparison.OrdinalIgnoreCase)
                ? StartupRegistrationStatus.LegacyArgument
                : StartupRegistrationStatus.StaleExecutable;
        }
        return StartupRegistrationStatus.StaleExecutable;
    }

    /// <summary>One concise sentence for the controller window.</summary>
    public static string Describe(StartupRegistrationState state) => state.Status switch
    {
        StartupRegistrationStatus.Disabled => "Windows sign-in restore is off.",
        StartupRegistrationStatus.Current => "Windows sign-in restore points at this app.",
        StartupRegistrationStatus.LegacyArgument =>
            "Windows sign-in restore was written by an older version. Repair it to use the current restore mode.",
        StartupRegistrationStatus.StaleExecutable =>
            "Windows sign-in restore points at a previous location of this app. Repair it so sign-in restore keeps working.",
        StartupRegistrationStatus.Unreadable =>
            "The Windows sign-in restore entry could not be read. Turning it off and on again rewrites it.",
        _ => "Windows sign-in restore is unavailable on this platform."
    };

    private static ControllerInvocation? TryBuildCurrentInvocation()
    {
        try
        {
            return BuildCurrentInvocation();
        }
        catch
        {
            return null;
        }
    }

    private static ControllerInvocation BuildCurrentInvocation() =>
        ControllerInvocation.ForCurrentProcess(CurrentArgument);
}
