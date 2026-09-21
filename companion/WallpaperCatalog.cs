using Microsoft.Win32;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexWallpaperSkin;

public static class WallpaperCatalog
{
    public const long MaximumImageBytes = 32L * 1024 * 1024;
    // Modern 1440p/4K Workshop videos routinely exceed 128 MiB. Keep a bounded
    // ceiling, but allow the original H.264 files used by Wallpaper Engine so
    // they are not silently replaced by a low-resolution thumbnail/GIF.
    public const long MaximumVideoBytes = 256L * 1024 * 1024;
    public const long MaximumNativeVideoBytes = 8L * 1024 * 1024 * 1024;
    public const int MaximumImageDimension = 8192;
    public const long MaximumImagePixels = 33_554_432;
    private const long MaximumProjectJsonBytes = 1024 * 1024;
    private const long MaximumLibraryFoldersBytes = 4L * 1024 * 1024;
    private const int MaximumProjectsPerScan = 5_000;
    private const int MaximumDirectoriesPerScan = 20_000;
    private const int MaximumScanDepth = 16;
    private const int MaximumCatalogPathChars = 2048;
    private const long MaximumDiscoveredPathChars = 2_000_000;
    private const int MaximumProjectTitleChars = 256;
    private const int MaximumProjectTypeChars = 32;
    private const int MaximumRelativeMediaPathChars = 1024;
    private const int MaximumSteamLibraryRoots = 16;
    private const int MaximumWorkshopRoots = 32;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm"
    };

    public static WallpaperEntry CreateLocal(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > MaximumCatalogPathChars)
        {
            throw new InvalidDataException("The wallpaper path is too long for the catalog.");
        }
        var extension = Path.GetExtension(fullPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Wallpaper file was not found.", fullPath);
        }

        var kind = ImageExtensions.Contains(extension)
            ? WallpaperKind.Image
            : VideoExtensions.Contains(extension)
                ? WallpaperKind.Video
                : WallpaperKind.Unknown;
        if (kind == WallpaperKind.Unknown)
        {
            throw new NotSupportedException("Supported local formats: PNG, JPG, JPEG, WebP, GIF, MP4 and WebM.");
        }
        ValidateMediaFile(fullPath, kind == WallpaperKind.Video);

        return new WallpaperEntry
        {
            Id = "local:" + fullPath.ToLowerInvariant(),
            Title = Path.GetFileNameWithoutExtension(fullPath),
            Source = "Local file",
            MediaPath = fullPath,
            Kind = kind,
            Support = WallpaperSupport.Direct,
            Note = kind == WallpaperKind.Video
                ? "Direct video background. Muted by default."
                : "Direct image background."
        };
    }

    public static IReadOnlyList<string> DiscoverWorkshopRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            steamRoots.Add(Path.Combine(programFilesX86, "Steam"));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            steamRoots.Add(Path.Combine(programFiles, "Steam"));
        }

        foreach (var registryPath in new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Valve\Steam"
        })
        {
            foreach (var valueName in new[] { "SteamPath", "InstallPath" })
            {
                if (Registry.GetValue(registryPath, valueName, null) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    var candidate = value.Replace('/', Path.DirectorySeparatorChar);
                    if (candidate.Length <= MaximumCatalogPathChars && steamRoots.Count < MaximumSteamLibraryRoots)
                    {
                        steamRoots.Add(candidate);
                    }
                }
            }
        }

        foreach (var steamRoot in steamRoots.ToArray())
        {
            var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
            {
                continue;
            }

            try
            {
                var vdfInfo = new FileInfo(vdfPath);
                if (vdfInfo.Length <= 0 || vdfInfo.Length > MaximumLibraryFoldersBytes)
                {
                    continue;
                }
                var vdf = File.ReadAllText(vdfPath);
                foreach (Match match in Regex.Matches(vdf, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                {
                    var value = match.Groups["path"].Value.Replace("\\\\", "\\");
                    if (!string.IsNullOrWhiteSpace(value)
                        && value.Length <= MaximumCatalogPathChars
                        && steamRoots.Count < MaximumSteamLibraryRoots)
                    {
                        steamRoots.Add(value);
                    }
                }
            }
            catch
            {
                // A locked or malformed Steam config should not prevent manual selection.
            }
        }

        foreach (var steamRoot in steamRoots.Take(MaximumSteamLibraryRoots))
        {
            var workshop = Path.Combine(steamRoot, "steamapps", "workshop", "content", "431960");
            if (Directory.Exists(workshop))
            {
                roots.Add(Path.GetFullPath(workshop));
            }
            var myProjects = Path.Combine(steamRoot, "steamapps", "common", "wallpaper_engine", "projects", "myprojects");
            if (Directory.Exists(myProjects))
            {
                roots.Add(Path.GetFullPath(myProjects));
            }
        }

        return roots
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumWorkshopRoots)
            .ToArray();
    }

    public static IReadOnlyList<WallpaperEntry> ScanWorkshopRoot(string root) =>
        ScanWorkshopRoots([root], CancellationToken.None);

    public static IReadOnlyList<WallpaperEntry> ScanWorkshopRoots(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeWorkshopRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumWorkshopRoots + 1)
            .ToArray();
        if (fullRoots.Length == 0)
        {
            return [];
        }
        if (fullRoots.Length > MaximumWorkshopRoots)
        {
            throw new InvalidDataException($"At most {MaximumWorkshopRoots} Wallpaper Engine roots can be scanned at once.");
        }
        foreach (var fullRoot in fullRoots)
        {
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException($"Wallpaper Engine workshop folder was not found: {fullRoot}");
            }
        }

        var results = new List<WallpaperEntry>();
        var budget = new CatalogScanBudget();
        foreach (var fullRoot in fullRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPaths = TryGetOfficialWorkshopManifest(fullRoot, out var manifestPath)
                ? EnumerateDownloadedSubscriptionProjects(
                    fullRoot,
                    SteamWorkshopManifest.ReadDownloadedSubscriptions(manifestPath),
                    budget,
                    cancellationToken)
                : EnumerateProjectFilesSafe(fullRoot, budget, cancellationToken);
            foreach (var projectPath in projectPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    results.Add(ParseProject(projectPath));
                }
                catch (Exception exception)
                {
                    results.Add(new WallpaperEntry
                    {
                        Id = "workshop-error:" + projectPath.ToLowerInvariant(),
                        Title = Path.GetFileName(Path.GetDirectoryName(projectPath)) ?? "Unknown workshop item",
                        Source = "Wallpaper Engine",
                        ProjectPath = projectPath,
                        Kind = WallpaperKind.Unknown,
                        Support = WallpaperSupport.Rejected,
                        Note = "Could not read project.json: " + exception.Message
                    });
                }
            }
        }

        return results.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool TryGetOfficialWorkshopManifest(string root, out string manifestPath)
    {
        manifestPath = string.Empty;
        var directory = new DirectoryInfo(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
        if (!directory.Name.Equals("431960", StringComparison.OrdinalIgnoreCase)
            || directory.Parent?.Name.Equals("content", StringComparison.OrdinalIgnoreCase) != true
            || directory.Parent.Parent?.Name.Equals("workshop", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }
        manifestPath = Path.Combine(directory.Parent.Parent.FullName, "appworkshop_431960.acf");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "Steam's Wallpaper Engine subscription manifest is unavailable. The existing catalog was left unchanged.",
                manifestPath);
        }
        return true;
    }

    private static IEnumerable<string> EnumerateDownloadedSubscriptionProjects(
        string root,
        IReadOnlySet<string> eligibleIds,
        CatalogScanBudget budget,
        CancellationToken cancellationToken)
    {
        foreach (var id in eligibleIds.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(root, id);
            if (!Directory.Exists(directory)) continue;
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            budget.AddDirectory(directory);
            var project = Path.Combine(directory, "project.json");
            if (!File.Exists(project)) continue;
            budget.AddProject();
            yield return project;
        }
    }

    public static WallpaperEntry ParseProject(string projectPath)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (fullProjectPath.Length > MaximumCatalogPathChars)
        {
            throw new InvalidDataException("The Wallpaper Engine project path is too long for the catalog.");
        }
        var projectInfo = new FileInfo(fullProjectPath);
        if (!projectInfo.Exists)
        {
            throw new FileNotFoundException("Wallpaper Engine project.json was not found.", fullProjectPath);
        }
        if ((projectInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Reparse-point Wallpaper Engine project files are not accepted.");
        }
        var projectDirectory = Path.GetDirectoryName(fullProjectPath)
            ?? throw new InvalidDataException("project.json has no parent folder.");
        using var stream = new FileStream(fullProjectPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan
        });
        if (stream.Length <= 0 || stream.Length > MaximumProjectJsonBytes)
        {
            throw new InvalidDataException("Wallpaper Engine project.json must be between 1 byte and 1 MiB.");
        }
        using var document = JsonDocument.Parse(stream);
        return ParseProjectDocument(document.RootElement, projectDirectory, fullProjectPath);
    }

    internal static WallpaperEntry ParseProjectDocument(JsonElement root, string projectDirectory, string projectPath)
    {
        var typeText = GetBoundedString(root, "type", MaximumProjectTypeChars)?.Trim().ToLowerInvariant() ?? "unknown";
        var kind = typeText switch
        {
            "image" => WallpaperKind.Image,
            "video" => WallpaperKind.Video,
            "scene" => WallpaperKind.Scene,
            "web" => WallpaperKind.Web,
            "application" => WallpaperKind.Application,
            _ => WallpaperKind.Unknown
        };
        var title = NormalizeTitle(GetBoundedString(root, "title", MaximumProjectTitleChars)
            ?? Path.GetFileName(projectDirectory)
            ?? "Untitled");
        var fileName = GetBoundedString(root, "file", MaximumRelativeMediaPathChars);
        var previewName = GetBoundedString(root, "preview", MaximumRelativeMediaPathChars);
        var mediaPath = ResolveContainedFile(projectDirectory, fileName);
        var previewPath = ResolveContainedFile(projectDirectory, previewName);
        if (!IsValidMedia(previewPath, ImageExtensions, isVideo: false))
        {
            previewPath = FindPreview(projectDirectory);
        }

        WallpaperSupport support;
        var preferNativeCapture = false;
        string note;
        switch (kind)
        {
            case WallpaperKind.Image:
                support = IsValidMedia(mediaPath, ImageExtensions, isVideo: false)
                    ? WallpaperSupport.Direct
                    : PreviewSupportFor(previewPath);
                note = support == WallpaperSupport.Direct
                    ? "Wallpaper Engine image loaded directly."
                    : support is WallpaperSupport.StaticPreview or WallpaperSupport.AnimatedPreview
                        ? "Image source was unavailable; using its validated preview."
                        : "No supported image or preview was found.";
                break;
            case WallpaperKind.Video:
                var directVideo = IsValidMedia(mediaPath, VideoExtensions, isVideo: true);
                preferNativeCapture = !directVideo && IsValidNativeVideo(mediaPath);
                support = directVideo || preferNativeCapture
                    ? WallpaperSupport.Direct
                    : PreviewSupportFor(previewPath);
                note = preferNativeCapture
                    ? "Large Wallpaper Engine video will use native play-in-window capture without uploading the whole file into Codex."
                    : support == WallpaperSupport.Direct
                    ? "Wallpaper Engine video loaded directly."
                    : support is WallpaperSupport.StaticPreview or WallpaperSupport.AnimatedPreview
                        ? "Video source was unavailable; using its validated preview."
                        : "No supported MP4/WebM or preview was found.";
                break;
            case WallpaperKind.Scene:
                var scenePackagePath = ResolveContainedFile(projectDirectory, "scene.pkg");
                if (ScenePackageValidator.TryValidate(scenePackagePath, out var packageInfo))
                {
                    mediaPath = scenePackagePath;
                    support = WallpaperSupport.LiveScene;
                    note = $"Wallpaper Engine native rendering is preferred. Safe built-in 2D fallback is available ({packageInfo!.Version}, {packageInfo.EntryCount:N0} assets); unsupported fallback features are omitted.";
                }
                else
                {
                    support = PreviewSupportFor(previewPath);
                    note = support switch
                    {
                        WallpaperSupport.AnimatedPreview => "Native Wallpaper Engine rendering will be attempted; the low-resolution animated Workshop preview is only the final fallback.",
                        WallpaperSupport.StaticPreview => "Native Wallpaper Engine rendering will be attempted; the static Workshop preview is only the final fallback.",
                        _ => "Native Wallpaper Engine rendering will be attempted; no safe browser-side fallback is available."
                    };
                }
                break;
            case WallpaperKind.Web:
                support = PreviewSupportFor(previewPath);
                note = support switch
                {
                    WallpaperSupport.AnimatedPreview => "Web wallpaper code is not executed; using its animated Workshop preview.",
                    WallpaperSupport.StaticPreview => "Web wallpaper code is not executed; using its static Workshop preview.",
                    _ => "Web project rejected because it has no safe preview."
                };
                break;
            case WallpaperKind.Application:
                support = WallpaperSupport.Rejected;
                note = "Application wallpapers are rejected: executable content is never launched.";
                break;
            default:
                support = WallpaperSupport.Rejected;
                note = $"Unsupported Wallpaper Engine project type: {typeText}.";
                break;
        }

        return new WallpaperEntry
        {
            Id = "wallpaper-engine:" + Path.GetFullPath(projectPath).ToLowerInvariant(),
            Title = title,
            Source = "Wallpaper Engine",
            ProjectPath = projectPath,
            MediaPath = mediaPath,
            PreviewPath = previewPath,
            Kind = kind,
            Support = support,
            PreferNativeCapture = preferNativeCapture,
            Note = note
        };
    }

    public static string NormalizeWorkshopRoot(string input)
    {
        var fullPath = Path.GetFullPath(input.Trim());
        var directProject = Path.Combine(fullPath, "project.json");
        if (File.Exists(directProject))
        {
            return fullPath;
        }

        var nested = Path.Combine(fullPath, "steamapps", "workshop", "content", "431960");
        return Directory.Exists(nested) ? nested : fullPath;
    }

    public static string MimeTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".pkg" => "application/x-wallpaper-engine-scene",
            _ => "application/octet-stream"
        };
    }

    public static void ValidateMediaFile(string path, bool isVideo)
    {
        using var stream = OpenValidatedMediaFile(path, isVideo);
    }

    public static FileStream OpenValidatedMediaFile(WallpaperEntry wallpaper)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        var path = wallpaper.EffectivePath;
        if (!wallpaper.CanApply || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("The selected wallpaper has no applicable media path.");
        }
        if (wallpaper.Source.Equals("Wallpaper Engine", StringComparison.OrdinalIgnoreCase))
        {
            EnsureWallpaperEngineMediaStillContained(wallpaper.ProjectPath, path);
        }
        return wallpaper.IsScene
            ? ScenePackageValidator.OpenValidated(path)
            : OpenValidatedMediaFile(path, wallpaper.IsVideo);
    }

    public static FileStream OpenValidatedPreviewFile(WallpaperEntry wallpaper)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        var path = wallpaper.PreviewPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("The selected wallpaper has no validated preview fallback.");
        }
        if (wallpaper.Source.Equals("Wallpaper Engine", StringComparison.OrdinalIgnoreCase))
        {
            EnsureWallpaperEngineMediaStillContained(wallpaper.ProjectPath, path);
        }
        return OpenValidatedMediaFile(path, isVideo: false);
    }

    public static FileStream OpenValidatedMediaFile(string path, bool isVideo)
        => OpenValidatedMediaFile(path, isVideo, isVideo ? MaximumVideoBytes : MaximumImageBytes);

    public static void ValidateNativeWallpaperEngineVideo(WallpaperEntry wallpaper)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        if (!wallpaper.IsWallpaperEngineProject
            || wallpaper.Kind != WallpaperKind.Video
            || string.IsNullOrWhiteSpace(wallpaper.MediaPath))
        {
            throw new InvalidDataException("The selected item is not a contained Wallpaper Engine video.");
        }
        EnsureWallpaperEngineMediaStillContained(wallpaper.ProjectPath, wallpaper.MediaPath);
        using var stream = OpenValidatedMediaFile(wallpaper.MediaPath, isVideo: true, MaximumNativeVideoBytes);
    }

    private static FileStream OpenValidatedMediaFile(string path, bool isVideo, long maximumBytes)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Wallpaper media was not found.", fullPath);
        }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Reparse-point wallpaper media is not accepted.");
        }

        var stream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan
        });
        try
        {
            ValidateMediaStream(stream, file.Extension.ToLowerInvariant(), isVideo, maximumBytes);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateMediaStream(Stream stream, string extension, bool isVideo, long maximum)
    {
        if (stream.Length <= 0 || stream.Length > maximum)
        {
            throw new InvalidDataException($"Wallpaper media must be between 1 byte and {maximum / (1024 * 1024)} MiB.");
        }

        Span<byte> header = stackalloc byte[32];
        stream.Position = 0;
        var read = stream.Read(header);
        var valid = extension switch
        {
            ".png" => read >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".webp" => read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8),
            ".gif" => read >= 10 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)),
            ".mp4" => read >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8),
            ".webm" => read >= 4 && header[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }),
            _ => false
        };
        if (!valid || (isVideo ? !VideoExtensions.Contains(extension) : !ImageExtensions.Contains(extension)))
        {
            throw new InvalidDataException("The file signature does not match a supported wallpaper format.");
        }
        if (!isVideo)
        {
            var (width, height) = ReadImageDimensions(stream, extension);
            if (width <= 0 || height <= 0
                || width > MaximumImageDimension || height > MaximumImageDimension
                || checked(width * height) > MaximumImagePixels)
            {
                throw new InvalidDataException(
                    $"Wallpaper images are limited to {MaximumImageDimension:N0} pixels per side and {MaximumImagePixels:N0} total pixels; found {width:N0}×{height:N0}.");
            }
        }
    }

    internal static bool ShouldUseNativeVideoCapture(long fileLength) =>
        fileLength > MaximumVideoBytes && fileLength <= MaximumNativeVideoBytes;

    private static bool IsValidNativeVideo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var file = new FileInfo(path);
            if (!ShouldUseNativeVideoCapture(file.Length)) return false;
            using var stream = OpenValidatedMediaFile(path, isVideo: true, MaximumNativeVideoBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (long Width, long Height) ReadImageDimensions(Stream stream, string extension)
    {
        stream.Position = 0;
        Span<byte> header = stackalloc byte[32];
        var read = stream.Read(header);
        if (extension == ".png" && read >= 24 && header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return (BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4)));
        }
        if (extension is ".jpg" or ".jpeg")
        {
            return ReadJpegDimensions(stream);
        }
        if (extension == ".webp" && read >= 25)
        {
            if (header.Slice(12, 4).SequenceEqual("VP8X"u8) && read >= 30)
            {
                var width = 1L + header[24] + (header[25] << 8) + (header[26] << 16);
                var height = 1L + header[27] + (header[28] << 8) + (header[29] << 16);
                return (width, height);
            }
            if (header.Slice(12, 4).SequenceEqual("VP8L"u8) && header[20] == 0x2F)
            {
                var width = 1L + header[21] + ((header[22] & 0x3F) << 8);
                var height = 1L + (header[22] >> 6) + (header[23] << 2) + ((header[24] & 0x0F) << 10);
                return (width, height);
            }
            if (header.Slice(12, 4).SequenceEqual("VP8 "u8) && read >= 30
                && header[23] == 0x9D && header[24] == 0x01 && header[25] == 0x2A)
            {
                var width = (long)BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2)) & 0x3FFF;
                var height = (long)BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2)) & 0x3FFF;
                return (width, height);
            }
        }
        if (extension == ".gif" && read >= 10)
        {
            return ReadAndValidateGif(stream);
        }
        throw new InvalidDataException("The wallpaper image dimensions could not be validated.");
    }

    private static (long Width, long Height) ReadAndValidateGif(Stream stream)
    {
        const int maximumFrames = 500;
        const long maximumDecodedPixels = 64L * 1024 * 1024;
        stream.Position = 0;
        Span<byte> logical = stackalloc byte[13];
        ReadExactly(stream, logical);
        if (!(logical[..6].SequenceEqual("GIF87a"u8) || logical[..6].SequenceEqual("GIF89a"u8)))
        {
            throw new InvalidDataException("The GIF signature is invalid.");
        }
        var width = BinaryPrimitives.ReadUInt16LittleEndian(logical.Slice(6, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(logical.Slice(8, 2));
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("The GIF logical dimensions are invalid.");
        }
        if ((logical[10] & 0x80) != 0)
        {
            SkipExactly(stream, 3L * (1 << ((logical[10] & 0x07) + 1)));
        }

        var frames = 0;
        long decodedPixels = 0;
        var trailerFound = false;
        var descriptor = new byte[9];
        while (stream.Position < stream.Length)
        {
            var marker = stream.ReadByte();
            switch (marker)
            {
                case 0x3B:
                    trailerFound = true;
                    stream.Position = stream.Length;
                    break;
                case 0x21:
                    if (stream.ReadByte() < 0)
                    {
                        throw new EndOfStreamException("GIF extension label is truncated.");
                    }
                    SkipGifSubBlocks(stream);
                    break;
                case 0x2C:
                    ReadExactly(stream, descriptor);
                    var left = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(0, 2));
                    var top = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2, 2));
                    var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(4, 2));
                    var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(6, 2));
                    if (frameWidth == 0 || frameHeight == 0
                        || left + frameWidth > width || top + frameHeight > height)
                    {
                        throw new InvalidDataException("GIF frame dimensions escape its logical canvas.");
                    }
                    if ((descriptor[8] & 0x80) != 0)
                    {
                        SkipExactly(stream, 3L * (1 << ((descriptor[8] & 0x07) + 1)));
                    }
                    var lzwMinimumCodeSize = stream.ReadByte();
                    if (lzwMinimumCodeSize is < 2 or > 12)
                    {
                        throw new InvalidDataException("GIF contains an invalid LZW code size.");
                    }
                    SkipGifSubBlocks(stream);
                    frames++;
                    decodedPixels = checked(decodedPixels + (long)frameWidth * frameHeight);
                    if (frames > maximumFrames || decodedPixels > maximumDecodedPixels)
                    {
                        throw new InvalidDataException("GIF animation exceeds the frame or decoded-pixel safety limit.");
                    }
                    break;
                default:
                    throw new InvalidDataException("GIF contains an unknown or truncated block.");
            }
        }
        if (!trailerFound || frames == 0)
        {
            throw new InvalidDataException("GIF is incomplete or contains no image frame.");
        }
        return (width, height);
    }

    private static void SkipGifSubBlocks(Stream stream)
    {
        while (true)
        {
            var length = stream.ReadByte();
            if (length < 0)
            {
                throw new EndOfStreamException("GIF data sub-block is truncated.");
            }
            if (length == 0)
            {
                return;
            }
            SkipExactly(stream, length);
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException("Wallpaper media ended unexpectedly.");
            }
            offset += read;
        }
    }

    private static void SkipExactly(Stream stream, long count)
    {
        if (count < 0 || stream.Position + count > stream.Length)
        {
            throw new EndOfStreamException("Wallpaper media block exceeds the file.");
        }
        stream.Seek(count, SeekOrigin.Current);
    }

    private static (long Width, long Height) ReadJpegDimensions(Stream stream)
    {
        stream.Position = 2;
        var inspectionLimit = Math.Min(stream.Length, 1024 * 1024);
        while (stream.Position < inspectionLimit)
        {
            var prefix = stream.ReadByte();
            if (prefix < 0)
            {
                break;
            }
            if (prefix != 0xFF)
            {
                continue;
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF);
            if (marker < 0 || marker == 0xD9 || marker == 0xDA)
            {
                break;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD8)
            {
                continue;
            }

            var lengthHigh = stream.ReadByte();
            var lengthLow = stream.ReadByte();
            if (lengthHigh < 0 || lengthLow < 0)
            {
                break;
            }
            var segmentLength = (lengthHigh << 8) | lengthLow;
            if (segmentLength < 2 || stream.Position + segmentLength - 2 > stream.Length)
            {
                break;
            }
            var isStartOfFrame = marker is >= 0xC0 and <= 0xC3
                or >= 0xC5 and <= 0xC7
                or >= 0xC9 and <= 0xCB
                or >= 0xCD and <= 0xCF;
            if (isStartOfFrame && segmentLength >= 7)
            {
                _ = stream.ReadByte();
                var heightHigh = stream.ReadByte();
                var heightLow = stream.ReadByte();
                var widthHigh = stream.ReadByte();
                var widthLow = stream.ReadByte();
                if (heightHigh < 0 || heightLow < 0 || widthHigh < 0 || widthLow < 0)
                {
                    break;
                }
                return ((widthHigh << 8) | widthLow, (heightHigh << 8) | heightLow);
            }
            stream.Seek(segmentLength - 2, SeekOrigin.Current);
        }
        throw new InvalidDataException("The JPEG dimensions could not be validated within the first 1 MiB.");
    }

    private static string? GetBoundedString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = value.GetString();
        if (text is not null && text.Length > maximumLength)
        {
            throw new InvalidDataException($"Wallpaper Engine project field '{name}' exceeds {maximumLength:N0} characters.");
        }
        return text;
    }

    private static string NormalizeTitle(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "Untitled" : normalized;
    }

    private static string? ResolveContainedFile(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate)
                && !ContainsReparsePoint(normalizedRoot, candidate)
                ? candidate
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindPreview(string root)
    {
        foreach (var name in new[] { "preview.jpg", "preview.jpeg", "preview.png", "preview.webp", "preview.gif" })
        {
            var candidate = Path.Combine(root, name);
            if (IsValidMedia(candidate, ImageExtensions, isVideo: false))
            {
                return candidate;
            }
        }
        return null;
    }

    private static WallpaperSupport PreviewSupportFor(string? previewPath)
    {
        if (previewPath is null)
        {
            return WallpaperSupport.Rejected;
        }
        return Path.GetExtension(previewPath).Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? WallpaperSupport.AnimatedPreview
            : WallpaperSupport.StaticPreview;
    }

    private static bool IsValidMedia(string? path, HashSet<string> allowed, bool isVideo)
    {
        if (path is null || !File.Exists(path) || !allowed.Contains(Path.GetExtension(path)))
        {
            return false;
        }
        try
        {
            ValidateMediaFile(path, isVideo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateProjectFilesSafe(
        string root,
        CatalogScanBudget budget,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pending = new Stack<(string Path, int Depth)>();
        var rootWithoutSlash = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar);
        budget.AddDirectory(rootWithoutSlash);
        pending.Push((rootWithoutSlash, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(directory);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            var project = Path.Combine(directory, "project.json");
            if (File.Exists(project))
            {
                budget.AddProject();
                yield return project;
                continue;
            }
            if (depth >= MaximumScanDepth)
            {
                continue;
            }
            foreach (var child in EnumerateDirectoriesSafe(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullChild = Path.GetFullPath(child);
                if (fullChild.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    budget.AddDirectory(fullChild);
                    pending.Push((fullChild, depth + 1));
                }
            }
        }
    }

    private sealed class CatalogScanBudget
    {
        private int _directories;
        private int _projects;
        private long _pathCharacters;

        public void AddDirectory(string path)
        {
            if (path.Length > MaximumCatalogPathChars)
            {
                throw new InvalidDataException("Wallpaper Engine scan encountered an excessively long directory path.");
            }
            _directories++;
            _pathCharacters += path.Length;
            if (_directories > MaximumDirectoriesPerScan)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine scan found more than {MaximumDirectoriesPerScan:N0} directories across all roots. Choose a narrower folder.");
            }
            if (_pathCharacters > MaximumDiscoveredPathChars)
            {
                throw new InvalidDataException("Wallpaper Engine scan exceeded its shared path-metadata budget. Choose a narrower folder.");
            }
        }

        public void AddProject()
        {
            _projects++;
            if (_projects > MaximumProjectsPerScan)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine scan found more than {MaximumProjectsPerScan:N0} projects across all roots. Choose a narrower folder.");
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directory)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            try
            {
                enumerator = Directory.EnumerateDirectories(directory).GetEnumerator();
            }
            catch
            {
                yield break;
            }

            while (true)
            {
                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch
                {
                    yield break;
                }
                if (!moved)
                {
                    yield break;
                }
                yield return enumerator.Current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static bool ContainsReparsePoint(string normalizedRoot, string candidate)
    {
        try
        {
            var file = new FileInfo(candidate);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            var current = file.Directory;
            var rootWithoutSlash = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar);
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                if (current.FullName.Equals(rootWithoutSlash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                current = current.Parent;
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static void EnsureWallpaperEngineMediaStillContained(string? projectPath, string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidDataException("The Wallpaper Engine entry has no project.json path. Rescan the catalog.");
        }
        var fullProjectPath = Path.GetFullPath(projectPath);
        var fullMediaPath = Path.GetFullPath(mediaPath);
        if (fullProjectPath.Length > MaximumCatalogPathChars || fullMediaPath.Length > MaximumCatalogPathChars
            || !Path.GetFileName(fullProjectPath).Equals("project.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Wallpaper Engine entry contains an invalid or excessively long path.");
        }
        var projectFile = new FileInfo(fullProjectPath);
        if (!projectFile.Exists || (projectFile.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Wallpaper Engine project.json is missing or is a reparse point. Rescan the catalog.");
        }
        var projectDirectory = projectFile.DirectoryName
            ?? throw new InvalidDataException("Wallpaper Engine project.json has no parent directory.");
        var normalizedRoot = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullMediaPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || ContainsReparsePoint(normalizedRoot, fullMediaPath))
        {
            throw new InvalidDataException("The Wallpaper Engine media path escaped its project folder or now crosses a reparse point. Rescan the catalog.");
        }
    }
}
