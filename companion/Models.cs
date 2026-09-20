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
    LiveScene
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
    public string Note { get; set; } = string.Empty;

    [JsonIgnore]
    public bool CanApply => Support != WallpaperSupport.Rejected && !string.IsNullOrWhiteSpace(EffectivePath);

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
            var badge = Support switch
            {
                WallpaperSupport.Direct => Kind == WallpaperKind.Video ? "VIDEO" : "IMAGE",
                WallpaperSupport.LiveScene => "WE LIVE SCENE",
                WallpaperSupport.AnimatedPreview => "ANIMATED PREVIEW",
                WallpaperSupport.StaticPreview => "STATIC FALLBACK",
                _ => "REJECTED"
            };
            return $"{Title}  [{badge}]";
        }
    }
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
    public int SceneFrameRate { get; set; } = 15;
    public double SceneResolutionScale { get; set; } = 1;

    public WallpaperSettings Normalize()
    {
        FocusX = Math.Clamp(FocusX, 0, 100);
        FocusY = Math.Clamp(FocusY, 0, 100);
        Opacity = Math.Clamp(Opacity, 0, 1);
        BlackOverlay = Math.Clamp(BlackOverlay, 0, 0.9);
        PaletteStrength = Math.Clamp(PaletteStrength, 0, 1);
        PanelOpacity = Math.Clamp(PanelOpacity, 0.2, 0.95);
        Blur = Math.Clamp(Blur, 0, 30);
        Brightness = Math.Clamp(Brightness, 0.5, 1.5);
        Contrast = Math.Clamp(Contrast, 0.5, 1.5);
        Saturation = Math.Clamp(Saturation, 0, 2);
        PlaybackRate = Math.Clamp(PlaybackRate, 0.25, 2);
        SceneFrameRate = SceneFrameRate <= 10 ? 10 : 15;
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
    public const int CurrentSchema = 6;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string CdpBaseUrl { get; set; } = CdpEndpoint.CreateUnusedLoopbackUrl();
    public string? Aumid { get; set; }
    public string? WallpaperEngineRoot { get; set; }
    public string? SelectedWallpaperId { get; set; }
    public string? LastAppliedWallpaperId { get; set; }
    public string? PendingWallpaperId { get; set; }
    public bool PendingActivation { get; set; }
    public DateTimeOffset? PendingQueuedAt { get; set; }
    public DateTimeOffset? PendingLastAttemptAt { get; set; }
    public int PendingAttempts { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public QueueFailureReason PendingLastFailure { get; set; }
    public bool AutoRestoreOnLaunch { get; set; } = true;
    public List<WallpaperEntry> Wallpapers { get; set; } = [];
    public WallpaperSettings Settings { get; set; } = new();
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
    public string ConnectionState { get; set; } = string.Empty;
    public bool WallpaperQueued { get; set; }
    public string QueueSummary { get; set; } = string.Empty;
    public string StartupRegistration { get; set; } = string.Empty;
    public string? StartupRegistrationExecutable { get; set; }
    public List<string> Notes { get; set; } = [];
}
