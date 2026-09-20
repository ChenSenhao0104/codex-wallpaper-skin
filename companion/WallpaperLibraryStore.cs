using System.Text;
using System.Text.Json;

namespace CodexWallpaperSkin;

public sealed class WallpaperPersonalization
{
    public string Id { get; set; } = string.Empty;
    public string? CustomTitle { get; set; }
    public string? Collection { get; set; }
}

public sealed class WallpaperLibraryDocument
{
    public int Version { get; set; } = 1;
    public List<WallpaperPersonalization> Items { get; set; } = [];
}

public static class WallpaperLibraryStore
{
    private const long MaximumLibraryBytes = 2L * 1024 * 1024;
    private const int MaximumItems = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string LibraryPath { get; } = Path.Combine(StateStore.StateDirectory, "library.json");
    public static bool SaveBlocked { get; private set; }
    public static string? LastLoadWarning { get; private set; }

    public static Dictionary<string, WallpaperPersonalization> Load()
    {
        SaveBlocked = false;
        LastLoadWarning = null;
        try
        {
            if (!File.Exists(LibraryPath))
            {
                return new Dictionary<string, WallpaperPersonalization>(StringComparer.OrdinalIgnoreCase);
            }
            var file = new FileInfo(LibraryPath);
            if (file.Length <= 0 || file.Length > MaximumLibraryBytes)
            {
                throw new InvalidDataException("The wallpaper library file must be between 1 byte and 2 MiB.");
            }
            var document = JsonSerializer.Deserialize<WallpaperLibraryDocument>(File.ReadAllText(LibraryPath), JsonOptions)
                ?? throw new InvalidDataException("The wallpaper library file is empty.");
            return Normalize(document);
        }
        catch (Exception exception)
        {
            SaveBlocked = true;
            LastLoadWarning = "Personal wallpaper names and collections could not be loaded, so the original library file was left untouched (" + exception.Message + ").";
            return new Dictionary<string, WallpaperPersonalization>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(IReadOnlyDictionary<string, WallpaperPersonalization> personalizations)
    {
        if (SaveBlocked)
        {
            throw new IOException("Saving the personal wallpaper library is disabled because its existing file could not be safely preserved.");
        }
        var document = new WallpaperLibraryDocument
        {
            Items = personalizations.Values
                .Where(item => !string.IsNullOrWhiteSpace(item.CustomTitle) || !string.IsNullOrWhiteSpace(item.Collection))
                .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        var normalized = Normalize(document);
        document.Items = normalized.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumLibraryBytes)
        {
            throw new InvalidDataException("The personal wallpaper library exceeds the 2 MiB safety limit.");
        }

        Directory.CreateDirectory(StateStore.StateDirectory);
        var temporaryPath = Path.Combine(StateStore.StateDirectory, $"library.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, LibraryPath, true);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    public static void Apply(
        WallpaperEntry wallpaper,
        IReadOnlyDictionary<string, WallpaperPersonalization> personalizations)
    {
        if (!personalizations.TryGetValue(wallpaper.Id, out var item)) return;
        wallpaper.CustomTitle = item.CustomTitle;
        wallpaper.Collection = item.Collection;
    }

    public static void Update(
        WallpaperEntry wallpaper,
        IDictionary<string, WallpaperPersonalization> personalizations)
    {
        if (string.IsNullOrWhiteSpace(wallpaper.CustomTitle) && string.IsNullOrWhiteSpace(wallpaper.Collection))
        {
            personalizations.Remove(wallpaper.Id);
            return;
        }
        personalizations[wallpaper.Id] = new WallpaperPersonalization
        {
            Id = wallpaper.Id,
            CustomTitle = wallpaper.CustomTitle,
            Collection = wallpaper.Collection
        };
    }

    private static Dictionary<string, WallpaperPersonalization> Normalize(WallpaperLibraryDocument document)
    {
        if (document.Version != 1)
        {
            throw new InvalidDataException($"Unsupported wallpaper library version {document.Version}.");
        }
        if (document.Items is null || document.Items.Count > MaximumItems)
        {
            throw new InvalidDataException($"The wallpaper library can contain at most {MaximumItems:N0} items.");
        }

        var result = new Dictionary<string, WallpaperPersonalization>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 2048 || result.ContainsKey(item.Id))
            {
                throw new InvalidDataException("The wallpaper library contains an invalid or duplicate identifier.");
            }
            var customTitle = string.IsNullOrWhiteSpace(item.CustomTitle)
                ? null
                : WallpaperLibrary.NormalizeCustomTitle(item.CustomTitle);
            var collection = WallpaperLibrary.NormalizeCollection(item.Collection);
            if (customTitle is null && collection is null) continue;
            result[item.Id] = new WallpaperPersonalization
            {
                Id = item.Id,
                CustomTitle = customTitle,
                Collection = collection
            };
        }
        return result;
    }
}
