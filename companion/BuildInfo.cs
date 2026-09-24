using System.Reflection;

namespace CodexWallpaperSkin;

public static class BuildInfo
{
    public static string DisplayVersion { get; } = ResolveDisplayVersion();

    private static string ResolveDisplayVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var metadataIndex = informational.IndexOf('+');
            return metadataIndex > 0 ? informational[..metadataIndex] : informational;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null) return "unknown";
        return version.Revision == 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : version.ToString();
    }
}
