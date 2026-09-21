using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexWallpaperSkin;

public enum WallpaperKind
{
    Image,
    Video,
    Scene,
    Web,
    Application,
    Unknown
}

public enum WallpaperSupport
{
    Direct,
    StaticPreview,
    Rejected,
    AnimatedPreview,
    LiveScene,
    // Written by the DeepSeek preview branch for Wallpaper Engine scenes that
    // are handed to Wallpaper Engine rather than parsed by the safe fallback.
    // The Codex branch already selects its native backend from ProjectPath and
    // Kind, so recognizing this value is sufficient to load the shared catalog.
    NativeScene
}

public enum WallpaperFit
{
    Cover,
    Contain,
    Fill,
    None,
    ScaleDown
}

public sealed class WallpaperEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled";
    public string Source { get; set; } = "Local";
    public string? ProjectPath { get; set; }
    public string? MediaPath { get; set; }
    public string? PreviewPath { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WallpaperKind Kind { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WallpaperSupport Support { get; set; }
    public bool PreferNativeCapture { get; set; }
    public string Note { get; set; } = string.Empty;
    // Personal catalog metadata is deliberately separate from the source title.
    // Rescanning a Workshop project can refresh its technical metadata without
    // overwriting the name and collection chosen by the user.
    [JsonIgnore]
    public string? CustomTitle { get; set; }
    [JsonIgnore]
    public string? Collection { get; set; }

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle;

    [JsonIgnore]
    public bool IsWallpaperEngineProject =>
        Source.Equals("Wallpaper Engine", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ProjectPath);

    [JsonIgnore]
    public bool IsWallpaperEngineScene => IsWallpaperEngineProject && Kind == WallpaperKind.Scene;

    [JsonIgnore]
    public bool UsesWallpaperEngineCapture => IsWallpaperEngineScene
        || (IsWallpaperEngineProject && Kind == WallpaperKind.Video && PreferNativeCapture);

    [JsonIgnore]
    public bool CanApply => UsesWallpaperEngineCapture
        || (Support != WallpaperSupport.Rejected && !string.IsNullOrWhiteSpace(EffectivePath));

    [JsonIgnore]
    public string? EffectivePath => Support is WallpaperSupport.Direct or WallpaperSupport.LiveScene
        ? MediaPath
        : PreviewPath;

    [JsonIgnore]
    public bool IsVideo => Support == WallpaperSupport.Direct && Kind == WallpaperKind.Video;

    [JsonIgnore]
    public bool IsScene => Support == WallpaperSupport.LiveScene && Kind == WallpaperKind.Scene;

    [JsonIgnore]
    public string MediaMode => IsScene ? "scene" : IsVideo ? "video" : "image";

    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            var badge = IsWallpaperEngineScene ? "WE SCENE"
                : UsesWallpaperEngineCapture ? "WE NATIVE VIDEO" : Support switch
            {
                WallpaperSupport.Direct => Kind == WallpaperKind.Video ? "VIDEO" : "IMAGE",
                WallpaperSupport.LiveScene => "WE LIVE SCENE",
                WallpaperSupport.AnimatedPreview => "ANIMATED PREVIEW",
                WallpaperSupport.StaticPreview => "STATIC FALLBACK",
                _ => "REJECTED"
            };
            var collection = string.IsNullOrWhiteSpace(Collection) ? string.Empty : $"[{Collection}] ";
            return $"{collection}{DisplayTitle}  [{badge}]";
        }
    }
}

public sealed class VisualPresetSettings
{
    public double Opacity { get; set; } = 1;
    public double BlackOverlay { get; set; }
    public double Brightness { get; set; } = 1.12;
    public double Contrast { get; set; } = 1.04;
    public double Saturation { get; set; } = 1.06;
    public double PanelOpacity { get; set; } = 0.45;
    public double Blur { get; set; }
    public double SceneResolutionScale { get; set; } = 1;

    public static VisualPresetSettings BuiltIn() => new();

    public VisualPresetSettings Normalize()
    {
        Opacity = Math.Clamp(Opacity, 0, 1);
        BlackOverlay = Math.Clamp(BlackOverlay, 0, 0.8);
        Brightness = Math.Clamp(Brightness, 0.5, 1.5);
        Contrast = Math.Clamp(Contrast, 0.5, 1.5);
        Saturation = Math.Clamp(Saturation, 0, 2);
        PanelOpacity = Math.Clamp(PanelOpacity, 0.2, 0.95);
        Blur = Math.Clamp(Blur, 0, 30);
        SceneResolutionScale = Math.Clamp(SceneResolutionScale, 0.5, 1);
        return this;
    }
}

public sealed class VisualPresetProfile
{
    public const string DefaultId = "brighter-high-clarity";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Visual preset";
    public VisualPresetSettings Settings { get; set; } = VisualPresetSettings.BuiltIn();

    public static VisualPresetProfile BuiltIn() => new()
    {
        Id = DefaultId,
        Name = "Brighter high-clarity",
        Settings = VisualPresetSettings.BuiltIn()
    };

    public VisualPresetProfile Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) || Id.Length > 64 ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "Visual preset" : Name.Trim();
        if (Name.Length > 64) Name = Name[..64];
        Settings ??= VisualPresetSettings.BuiltIn();
        Settings.Normalize();
        return this;
    }

    public VisualPresetProfile Copy() => new()
    {
        Id = Id,
        Name = Name,
        Settings = new VisualPresetSettings
        {
            Opacity = Settings.Opacity,
            BlackOverlay = Settings.BlackOverlay,
            Brightness = Settings.Brightness,
            Contrast = Settings.Contrast,
            Saturation = Settings.Saturation,
            PanelOpacity = Settings.PanelOpacity,
            Blur = Settings.Blur,
            SceneResolutionScale = Settings.SceneResolutionScale
        }
    };
}

public sealed class WallpaperSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WallpaperFit Fit { get; set; } = WallpaperFit.Cover;
    public double FocusX { get; set; } = 50;
    public double FocusY { get; set; } = 50;
    public double Opacity { get; set; } = 1;
    public double BlackOverlay { get; set; }
    public bool AutoPalette { get; set; } = true;
    public double PaletteStrength { get; set; } = 0.72;
    public double PanelOpacity { get; set; } = 0.72;
    public bool TintInterfaceText { get; set; } = true;
    public double Blur { get; set; }
    public double Brightness { get; set; } = 1;
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public double PlaybackRate { get; set; } = 1;
    public bool Muted { get; set; } = true;
    public bool PauseWhenHidden { get; set; } = true;
    public int SceneFrameRate { get; set; } = 60;
    public double SceneResolutionScale { get; set; } = 1;

    public WallpaperSettings Normalize()
    {
        FocusX = Math.Clamp(FocusX, -100, 200);
        FocusY = Math.Clamp(FocusY, -100, 200);
        Opacity = Math.Clamp(Opacity, 0, 1);
        BlackOverlay = Math.Clamp(BlackOverlay, 0, 0.9);
        PaletteStrength = Math.Clamp(PaletteStrength, 0, 1);
        PanelOpacity = Math.Clamp(PanelOpacity, 0.2, 0.95);
        Blur = Math.Clamp(Blur, 0, 30);
        Brightness = Math.Clamp(Brightness, 0.5, 1.5);
        Contrast = Math.Clamp(Contrast, 0.5, 1.5);
        Saturation = Math.Clamp(Saturation, 0, 2);
        PlaybackRate = Math.Clamp(PlaybackRate, 0.25, 2);
        SceneFrameRate = SceneFrameRate >= 60 ? 60 : 30;
        SceneResolutionScale = Math.Clamp(SceneResolutionScale, 0.5, 1);
        return this;
    }
}

public sealed class PaletteResult
{
    public string Dominant { get; set; } = string.Empty;
    public string Surface { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string AccentText { get; set; } = string.Empty;
    public double TextContrast { get; set; }
}

public sealed class AppState
{
    public const int CurrentSchema = 7;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string CdpBaseUrl { get; set; } = CdpEndpoint.CreateUnusedLoopbackUrl();
    public string? Aumid { get; set; }
    public string? WallpaperEngineRoot { get; set; }
    public string? SelectedWallpaperId { get; set; }
    public string? LastAppliedWallpaperId { get; set; }
    public string? PendingWallpaperId { get; set; }
    public bool PendingActivation { get; set; }
    public bool AutoRestoreOnLaunch { get; set; } = true;
    public List<WallpaperEntry> Wallpapers { get; set; } = [];
    public WallpaperSettings Settings { get; set; } = new();
    // Keep the selected profile mirrored here so v0.4.4 can still open and
    // update the state after someone evaluates the multi-preset build.
    public VisualPresetSettings? VisualPreset { get; set; } = VisualPresetSettings.BuiltIn();
    public List<VisualPresetProfile> VisualPresets { get; set; } = [VisualPresetProfile.BuiltIn()];
    public string? SelectedVisualPresetId { get; set; } = VisualPresetProfile.DefaultId;

    // Preview branches may add schema-6 queue metadata independently. Preserve
    // fields this branch does not interpret so alternating builds cannot erase
    // one another's state when they save the shared user profile.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record WallpaperApplyResult(PaletteResult? Palette, string Mode, string? Warning);

public sealed record CdpTarget(string Id, string Type, string Title, string Url, string WebSocketDebuggerUrl);

public sealed class DiagnosticReport
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Os { get; set; } = Environment.OSVersion.VersionString;
    public string Runtime { get; set; } = Environment.Version.ToString();
    public string Architecture { get; set; } = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string StatePath { get; set; } = StateStore.StatePath;
    public bool StateFileExists { get; set; }
    public string CdpEndpoint { get; set; } = string.Empty;
    public bool CdpEndpointIsLoopback { get; set; }
    public bool CdpReachable { get; set; }
    public string? CdpError { get; set; }
    public List<CdpTarget> Targets { get; set; } = [];
    public List<string> AumidCandidates { get; set; } = [];
    public string? SavedWallpaper { get; set; }
    public bool SavedWallpaperExists { get; set; }
    public int RequestedSceneFrameRate { get; set; }
    public int? WallpaperEngineFrameRateLimit { get; set; }
    public bool HardwareH264Available { get; set; }
    public List<string> HardwareH264Encoders { get; set; } = [];
    public string? HardwareH264ProbeError { get; set; }
    public List<string> Notes { get; set; } = [];
}
