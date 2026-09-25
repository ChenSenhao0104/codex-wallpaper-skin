using System.Text.RegularExpressions;

namespace CodexWallpaperSkin;

internal static class DiagnosticPrivacy
{
    public const string StatePath = @"%LOCALAPPDATA%\CodexWallpaperSkin\state.json";
    public const string LogPath = @"%LOCALAPPDATA%\CodexWallpaperSkin\controller.log";
    private static readonly Regex QuotedWindowsPath = new(
        "[\"'](?:[A-Za-z]:\\\\|\\\\\\\\)[^\"'\\r\\n]+[\"']",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex UnquotedWindowsPath = new(
        """(?i)(?<![A-Za-z0-9])(?:[A-Z]:\\|\\\\\?\\[A-Z]:\\|\\\\[^\\\s]+\\)[^\s"']+""",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static string? RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = value;
        foreach (var location in GetKnownLocations())
        {
            if (!string.IsNullOrWhiteSpace(location.Path))
            {
                result = result.Replace(location.Path, location.Token, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!string.IsNullOrWhiteSpace(Environment.UserName))
        {
            result = result.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
        }
        result = QuotedWindowsPath.Replace(result, "\"<local-path>\"");
        result = UnquotedWindowsPath.Replace(result, "<local-path>");
        return result;
    }

    public static CdpTarget SanitizeTarget(CdpTarget target) => target with
    {
        Id = "<redacted>",
        Title = CdpDiscovery.IsPrimaryCodexPage(target)
            ? "Codex main window"
            : target.Url.StartsWith("app://", StringComparison.OrdinalIgnoreCase)
                ? "Codex auxiliary window"
                : target.Type,
        Url = SanitizeTargetUrl(target.Url),
        WebSocketDebuggerUrl = "ws://127.0.0.1:<port>/devtools/page/<redacted>"
    };

    private static string SanitizeTargetUrl(string value)
    {
        if (value.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
        {
            var end = value.IndexOfAny(['?', '#']);
            return end < 0 ? value : value[..end];
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "<redacted>";
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{uri.Host}/<redacted>";
        }
        return $"{uri.Scheme}://<redacted>";
    }

    private static IEnumerable<(string Path, string Token)> GetKnownLocations()
    {
        var candidates = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "%TEMP%"),
            (AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "%APPDIR%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%")
        };
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Item1))
            .DistinctBy(candidate => candidate.Item1, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(candidate => candidate.Item1.Length)
            .Select(candidate => (candidate.Item1, candidate.Item2));
    }
}
