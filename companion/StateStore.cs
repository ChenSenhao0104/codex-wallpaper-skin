using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace CodexWallpaperSkin;

public static class StateStore
{
    private const long MaximumStateBytes = 4L * 1024 * 1024;
    private const int MaximumSavedWallpapers = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string StateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexWallpaperSkin");

    public static string StatePath { get; } = Path.Combine(StateDirectory, "state.json");
    public static bool SaveBlocked { get; private set; }
    public static string? LastLoadWarning { get; private set; }

    public static AppState Load()
    {
        SaveBlocked = false;
        LastLoadWarning = null;
        try
        {
            if (!File.Exists(StatePath))
            {
                return new AppState();
            }

            var stateFile = new FileInfo(StatePath);
            if (stateFile.Length <= 0 || stateFile.Length > MaximumStateBytes)
            {
                throw new InvalidDataException("The saved state must be between 1 byte and 4 MiB.");
            }
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), JsonOptions) ?? new AppState();
            if (state.SchemaVersion > AppState.CurrentSchema)
            {
                SaveBlocked = true;
                LastLoadWarning =
                    $"State schema {state.SchemaVersion} is newer than this companion supports ({AppState.CurrentSchema}). The original file was left untouched and saving is disabled for this session.";
                return NormalizeState(state);
            }
            MigrateState(state);
            return NormalizeState(state);
        }
        catch (Exception loadError)
        {
            SaveBlocked = true;
            LastLoadWarning =
                $"The saved state could not be read, so the original file was left untouched and saving is disabled for this session ({loadError.Message}).";
            return new AppState();
        }
    }

    public static void Save(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (SaveBlocked)
        {
            throw new IOException("Saving is disabled because the existing state file could not be safely preserved.");
        }
        ValidateStateForSave(state);
        state.Settings ??= new WallpaperSettings();
        state.Settings.Normalize();
        state.SchemaVersion = AppState.CurrentSchema;
        var json = JsonSerializer.Serialize(state, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumStateBytes)
        {
            throw new InvalidDataException("The saved wallpaper catalog exceeds the 4 MiB state limit.");
        }
        Directory.CreateDirectory(StateDirectory);
        var temporaryPath = Path.Combine(StateDirectory, $"state.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, StatePath, true);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static AppState NormalizeState(AppState state)
    {
        state.Settings ??= new WallpaperSettings();
        state.Settings.Normalize();
        state.Wallpapers ??= [];
        state.Wallpapers = state.Wallpapers
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id) && item.Id.Length <= 2048)
            .Take(MaximumSavedWallpapers)
            .Select(item =>
            {
                item.Title = Limit(item.Title, 256, "Untitled");
                item.Source = Limit(item.Source, 64, "Local");
                item.Note = Limit(item.Note, 1024, string.Empty);
                item.ProjectPath = BoundedPath(item.ProjectPath);
                item.MediaPath = BoundedPath(item.MediaPath);
                item.PreviewPath = BoundedPath(item.PreviewPath);
                return item;
            })
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (!CdpEndpoint.IsLoopbackHttp(state.CdpBaseUrl))
        {
            state.CdpBaseUrl = CdpEndpoint.CreateUnusedLoopbackUrl();
        }
        state.Aumid = state.Aumid is { Length: <= 256 } ? state.Aumid : null;
        state.WallpaperEngineRoot = BoundedPath(state.WallpaperEngineRoot);
        state.SelectedWallpaperId = state.SelectedWallpaperId is { Length: <= 2048 }
            ? state.SelectedWallpaperId
            : null;
        state.LastAppliedWallpaperId = state.LastAppliedWallpaperId is { Length: <= 2048 }
            ? state.LastAppliedWallpaperId
            : null;
        state.PendingWallpaperId = state.PendingWallpaperId is { Length: <= 2048 }
            ? state.PendingWallpaperId
            : null;
        if (string.IsNullOrWhiteSpace(state.PendingWallpaperId)) state.PendingActivation = false;
        return state;
    }

    private static void MigrateState(AppState state)
    {
        if (state.SchemaVersion < 3)
        {
            state.Settings ??= new WallpaperSettings();
            // v0.1.x shipped 86% media opacity plus an 18% full-screen black veil.
            // Migrate only the exact legacy defaults so deliberate user tuning remains intact.
            if (Math.Abs(state.Settings.Opacity - 0.86) < 0.000_001
                && Math.Abs(state.Settings.BlackOverlay - 0.18) < 0.000_001)
            {
                state.Settings.Opacity = 1;
                state.Settings.BlackOverlay = 0;
            }
            state.SchemaVersion = 3;
        }
        if (state.SchemaVersion < 4)
        {
            // Existing users have no remembered applied wallpaper yet. The
            // feature becomes active after their next successful Apply.
            state.LastAppliedWallpaperId = null;
            state.AutoRestoreOnLaunch = true;
            state.SchemaVersion = 4;
        }
        if (state.SchemaVersion < 5)
        {
            state.PendingWallpaperId = null;
            state.PendingActivation = false;
            state.SchemaVersion = 5;
        }
    }

    private static string Limit(string? value, int maximumLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static string? BoundedPath(string? value) =>
        value is { Length: > 0 and <= 2048 } ? value : null;

    internal static void ValidateStateForSave(AppState state)
    {
        if (state.Wallpapers is null)
        {
            throw new InvalidDataException("The wallpaper catalog is missing.");
        }
        if (state.Wallpapers.Count > MaximumSavedWallpapers)
        {
            throw new InvalidDataException(
                $"The wallpaper catalog contains {state.Wallpapers.Count:N0} entries; at most {MaximumSavedWallpapers:N0} can be saved. Remove entries or scan a narrower folder.");
        }
        if (!CdpEndpoint.IsLoopbackHttp(state.CdpBaseUrl))
        {
            throw new InvalidDataException("The saved CDP endpoint is not a canonical loopback URL.");
        }
        if (state.Aumid is { Length: > 256 }
            || state.WallpaperEngineRoot is { Length: > 2048 }
            || state.SelectedWallpaperId is { Length: > 2048 }
            || state.LastAppliedWallpaperId is { Length: > 2048 }
            || state.PendingWallpaperId is { Length: > 2048 })
        {
            throw new InvalidDataException("A saved identifier or path exceeds its safety limit.");
        }

        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in state.Wallpapers)
        {
            if (item is null
                || string.IsNullOrWhiteSpace(item.Id)
                || item.Id.Length > 2048
                || !identifiers.Add(item.Id))
            {
                throw new InvalidDataException("The wallpaper catalog contains a missing, duplicate, or excessively long identifier.");
            }
            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 256
                || string.IsNullOrWhiteSpace(item.Source) || item.Source.Length > 64
                || item.Note is null || item.Note.Length > 1024
                || item.ProjectPath is { Length: > 2048 }
                || item.MediaPath is { Length: > 2048 }
                || item.PreviewPath is { Length: > 2048 })
            {
                throw new InvalidDataException($"Wallpaper '{item.Id}' contains metadata that exceeds the catalog safety limits.");
            }
        }
    }
}
