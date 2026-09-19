using System.Text;
using System.Text.Json;

namespace CodexWallpaperSkin;

public sealed class SceneBrowserOptions
{
    public JsonElement? Project { get; init; }
    public Dictionary<string, string> Shaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string Renderer { get; init; } = SceneRuntimeSource.UpstreamRevision;
}

public static class SceneRuntimeAssets
{
    private const long MaximumProjectBytes = 1024 * 1024;
    private const int MaximumShaderFiles = 512;
    private const long MaximumShaderFileBytes = 128 * 1024;
    private const long MaximumShaderBytes = 2L * 1024 * 1024;
    private static readonly HashSet<string> ShaderExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".frag", ".vert", ".h"
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SceneBrowserOptions Load(WallpaperEntry wallpaper)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        if (!wallpaper.IsScene || string.IsNullOrWhiteSpace(wallpaper.ProjectPath))
        {
            throw new InvalidDataException("The selected entry is not a validated Wallpaper Engine scene.");
        }

        JsonElement? project = null;
        var projectFile = new FileInfo(Path.GetFullPath(wallpaper.ProjectPath));
        if (projectFile.Exists
            && (projectFile.Attributes & FileAttributes.ReparsePoint) == 0
            && projectFile.Length is > 0 and <= MaximumProjectBytes)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(projectFile.FullName), new JsonDocumentOptions
            {
                MaxDepth = 64,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            project = document.RootElement.Clone();
        }

        var shaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shaderRoot = FindShaderRoot(projectFile.Directory);
        if (shaderRoot is not null)
        {
            try
            {
                LoadShaders(shaderRoot, shaders);
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or DecoderFallbackException
                or OverflowException)
            {
                // Missing or malformed optional shader assets must not prevent the
                // package's original full-resolution texture from being used.
                shaders.Clear();
            }
        }
        return new SceneBrowserOptions { Project = project, Shaders = shaders };
    }

    internal static string? FindShaderRoot(DirectoryInfo? start)
    {
        var current = start;
        for (var depth = 0; depth < 10 && current is not null; depth++, current = current.Parent)
        {
            string candidate;
            if (current.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(current.FullName, "common", "wallpaper_engine", "assets", "shaders");
            }
            else if (current.Name.Equals("wallpaper_engine", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(current.FullName, "assets", "shaders");
            }
            else
            {
                continue;
            }
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static void LoadShaders(string root, Dictionary<string, string> destination)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (depth > 8 || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            foreach (var child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push((child, depth + 1));
                }
            }
            foreach (var file in directory.EnumerateFiles())
            {
                if (!ShaderExtensions.Contains(file.Extension)
                    || (file.Attributes & FileAttributes.ReparsePoint) != 0
                    || file.Length <= 0
                    || file.Length > MaximumShaderFileBytes)
                {
                    continue;
                }
                var fullPath = Path.GetFullPath(file.FullName);
                if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                totalBytes = checked(totalBytes + file.Length);
                if (destination.Count >= MaximumShaderFiles || totalBytes > MaximumShaderBytes)
                {
                    throw new InvalidDataException("Wallpaper Engine shader assets exceed the safe loading budget.");
                }
                var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/').ToLowerInvariant();
                var source = StrictUtf8.GetString(File.ReadAllBytes(fullPath));
                if (source.Contains('\0'))
                {
                    throw new InvalidDataException($"Wallpaper Engine shader '{relative}' contains invalid text data.");
                }
                destination.Add(relative, source);
            }
        }
    }
}
