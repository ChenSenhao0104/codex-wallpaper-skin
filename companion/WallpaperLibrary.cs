namespace CodexWallpaperSkin;

public static class WallpaperLibrary
{
    public const int MaximumCustomTitleLength = 256;
    public const int MaximumCollectionLength = 64;

    public static string NormalizeCustomTitle(string value)
    {
        var normalized = NormalizeSingleLine(value, MaximumCustomTitleLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidDataException("The custom wallpaper name cannot be empty. Use Restore original name to remove it.");
        }
        return normalized;
    }

    public static string? NormalizeCollection(string? value)
    {
        var normalized = NormalizeSingleLine(value, MaximumCollectionLength);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static void CopyPersonalization(WallpaperEntry previous, WallpaperEntry refreshed)
    {
        refreshed.CustomTitle = string.IsNullOrWhiteSpace(previous.CustomTitle)
            ? null
            : NormalizeCustomTitle(previous.CustomTitle);
        refreshed.Collection = NormalizeCollection(previous.Collection);
    }

    public static bool Matches(
        WallpaperEntry entry,
        string? searchText,
        WallpaperKind? kind,
        string? collection,
        bool ungrouped)
    {
        if (kind is not null && entry.Kind != kind)
        {
            return false;
        }
        if (ungrouped)
        {
            if (!string.IsNullOrWhiteSpace(entry.Collection)) return false;
        }
        else if (!string.IsNullOrWhiteSpace(collection)
            && !string.Equals(entry.Collection, collection, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = searchText?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        // The latest user-facing name is checked first. Keeping the source name
        // searchable makes it possible to recover an item after an accidental
        // rename without weakening the new-name behavior requested by users.
        return entry.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (!string.Equals(entry.DisplayTitle, entry.Title, StringComparison.CurrentCultureIgnoreCase)
                && entry.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            || (!string.IsNullOrWhiteSpace(entry.Collection)
                && entry.Collection.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string NormalizeSingleLine(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var singleLine = string.Join(' ', value
            .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
        if (singleLine.Length > maximumLength)
        {
            throw new InvalidDataException($"The value cannot exceed {maximumLength} characters.");
        }
        return singleLine;
    }
}
