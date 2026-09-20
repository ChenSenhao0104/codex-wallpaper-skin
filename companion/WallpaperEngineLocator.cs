namespace CodexWallpaperSkin;

/// <summary>
/// Finds the Wallpaper Engine installation that owns a Workshop project. Native
/// rendering eligibility depends only on this — never on our safe scene.pkg
/// parser — because Wallpaper Engine renders its own project format.
/// </summary>
public static class WallpaperEngineLocator
{
    public static bool TryResolveEngine(string projectPath, out string engineRoot, out string executable)
    {
        engineRoot = string.Empty;
        executable = string.Empty;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }
        try
        {
            var current = new FileInfo(Path.GetFullPath(projectPath)).Directory;
            for (var depth = 0; depth < 10 && current is not null; depth++, current = current.Parent)
            {
                if (!current.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
                var root = Path.Combine(current.FullName, "common", "wallpaper_engine");
                foreach (var name in new[] { "wallpaper64.exe", "wallpaper32.exe" })
                {
                    var candidate = Path.Combine(root, name);
                    if (File.Exists(candidate))
                    {
                        engineRoot = Path.GetFullPath(root);
                        executable = Path.GetFullPath(candidate);
                        return true;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return false;
    }

    public static bool IsEngineAvailable(string projectPath) =>
        TryResolveEngine(projectPath, out _, out _);
}
