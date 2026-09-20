using System.Reflection;

namespace CodexWallpaperSkin;

/// <summary>
/// How this controller process must be re-invoked later. It exists because the
/// Windows startup entry has to keep pointing at the executable that is actually
/// running: a published portable build, or <c>dotnet.exe</c> plus the companion
/// assembly during development.
/// </summary>
public sealed record ControllerInvocation(string Executable, string? AssemblyPath, string Argument)
{
    public static ControllerInvocation ForCurrentProcess(string argument)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("The controller executable path could not be determined.");
        }
        return new ControllerInvocation(executable, CurrentAssemblyPathFor(executable), argument);
    }

    /// <summary>
    /// A single-file published build must not record an assembly path, because
    /// its entry assembly is bundled inside the executable.
    /// </summary>
    internal static string? CurrentAssemblyPathFor(string executable)
    {
        if (!Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrWhiteSpace(assemblyPath) ? null : assemblyPath;
    }

    public string ToCommandLine()
    {
        var parts = new List<string> { Quote(Executable) };
        if (AssemblyPath is not null)
        {
            // Never record a dotnet invocation that cannot find its assembly.
            parts.Add(Quote(AssemblyPath));
        }
        parts.Add(Argument);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Compares the executable, the optional assembly and the argument. Paths are
    /// normalized so that a case-only or separator-only difference is not
    /// reported as a stale registration.
    /// </summary>
    public bool Matches(ControllerInvocation? other)
    {
        if (other is null)
        {
            return false;
        }
        return TargetsSameBinary(other)
            && Argument.Equals(other.Argument, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when both invocations run the same controller binary.</summary>
    public bool TargetsSameBinary(ControllerInvocation? other) =>
        other is not null
        && SamePath(Executable, other.Executable)
        && SameOptionalPath(AssemblyPath, other.AssemblyPath);

    public static ControllerInvocation? Parse(string? commandLine)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count < 2 || tokens[0].Length == 0)
        {
            return null;
        }
        string? assemblyPath = null;
        string? argument = null;
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                argument ??= token;
                continue;
            }
            if (assemblyPath is null && token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                assemblyPath = token;
            }
        }
        if (argument is null)
        {
            return null;
        }
        return new ControllerInvocation(tokens[0], assemblyPath, argument);
    }

    internal static IReadOnlyList<string> Tokenize(string? commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return tokens;
        }
        var builder = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(character))
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }
                continue;
            }
            builder.Append(character);
        }
        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }
        return tokens;
    }

    private static bool SamePath(string first, string second) =>
        NormalizePath(first).Equals(NormalizePath(second), StringComparison.OrdinalIgnoreCase);

    private static bool SameOptionalPath(string? first, string? second) =>
        first is null || second is null
            ? first is null && second is null
            : SamePath(first, second);

    private static string NormalizePath(string value)
    {
        var trimmed = value.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmed).TrimEnd('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed.TrimEnd('\\', '/');
        }
    }

    private static string Quote(string value) => "\"" + value + "\"";
}
