namespace CodexWallpaperSkin;

/// <summary>
/// The product-facing backend state required by section 5 of the specification.
/// It is deliberately separate from the apply "mode" strings, which describe how
/// one upload was performed.
/// </summary>
public enum WallpaperBackendStatus
{
    /// <summary>Nothing is applied.</summary>
    None,
    NativeDynamic,
    NativeDynamicReducedFrameRate,
    SafeSceneRenderer,
    AnimatedPreview,
    StaticPreview,
    DirectMedia,
    Unsupported,
    /// <summary>Native capture gave up; a labeled fallback was applied instead.</summary>
    CaptureFailed
}

/// <summary>A point-in-time view of the native capture stream.</summary>
public sealed record CaptureHealth(
    bool WindowAlive,
    int PublishedFrames,
    int RejectedFrames,
    int ConsecutiveRejections,
    double EffectiveFrameRate,
    int TargetFrameRate)
{
    public int SampleCount => PublishedFrames + RejectedFrames;
}

public static class BackendStatuses
{
    public static WallpaperBackendStatus FromApplyMode(string? mode) => mode switch
    {
        "wallpaper-engine-capture" => WallpaperBackendStatus.NativeDynamic,
        "live-scene" or "scene-partial" or "scene-static" => WallpaperBackendStatus.SafeSceneRenderer,
        "animated-preview" => WallpaperBackendStatus.AnimatedPreview,
        "static-preview" => WallpaperBackendStatus.StaticPreview,
        "video" or "image" => WallpaperBackendStatus.DirectMedia,
        _ => WallpaperBackendStatus.Unsupported
    };

    /// <summary>Short, user-facing name for the current backend.</summary>
    public static string Describe(WallpaperBackendStatus status) => status switch
    {
        WallpaperBackendStatus.NativeDynamic => "Native dynamic",
        WallpaperBackendStatus.NativeDynamicReducedFrameRate => "Native dynamic, reduced frame rate",
        WallpaperBackendStatus.SafeSceneRenderer => "Safe scene compatibility renderer",
        WallpaperBackendStatus.AnimatedPreview => "Animated Workshop preview",
        WallpaperBackendStatus.StaticPreview => "Static Workshop preview",
        WallpaperBackendStatus.DirectMedia => "Direct media",
        WallpaperBackendStatus.CaptureFailed => "Native capture failed",
        WallpaperBackendStatus.Unsupported => "Unsupported",
        _ => "Nothing applied"
    };

    /// <summary>True while Wallpaper Engine itself is the active renderer.</summary>
    public static bool IsNativeCapture(WallpaperBackendStatus status) =>
        status is WallpaperBackendStatus.NativeDynamic
            or WallpaperBackendStatus.NativeDynamicReducedFrameRate;

    /// <summary>True when the status represents a clearly labeled fallback.</summary>
    public static bool IsLabeledFallback(WallpaperBackendStatus status) =>
        status is WallpaperBackendStatus.SafeSceneRenderer
            or WallpaperBackendStatus.AnimatedPreview
            or WallpaperBackendStatus.StaticPreview;
}

/// <summary>
/// Decides when a native capture stream is healthy, degraded, or finished, and
/// how long to wait before retrying. Keeping this pure makes the recovery
/// behavior reproducible without a live Wallpaper Engine session.
/// </summary>
public static class CaptureRecoveryPolicy
{
    /// <summary>Consecutive unusable frames after which the stream gives up.</summary>
    public const int MaximumConsecutiveRejections = 30;

    /// <summary>Consecutive restart failures before a labeled fallback is applied.</summary>
    public const int MaximumRecoveryAttempts = 3;

    /// <summary>Below this fraction of the target rate the stream is reported as reduced.</summary>
    public const double ReducedFrameRateRatio = 0.7;

    /// <summary>Fewer samples than this cannot justify a degradation verdict.</summary>
    public const int MinimumSamplesForDegradation = 4;

    public static bool HasFailed(CaptureHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return !health.WindowAlive || health.ConsecutiveRejections >= MaximumConsecutiveRejections;
    }

    public static bool IsDegraded(CaptureHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (HasFailed(health) || health.TargetFrameRate <= 0)
        {
            return false;
        }
        if (health.SampleCount < MinimumSamplesForDegradation)
        {
            // A stream that just started has no meaningful rate yet; reporting it
            // as reduced would be a false alarm on every apply.
            return false;
        }
        return health.EffectiveFrameRate < health.TargetFrameRate * ReducedFrameRateRatio;
    }

    public static WallpaperBackendStatus Classify(CaptureHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (HasFailed(health))
        {
            return WallpaperBackendStatus.CaptureFailed;
        }
        return IsDegraded(health)
            ? WallpaperBackendStatus.NativeDynamicReducedFrameRate
            : WallpaperBackendStatus.NativeDynamic;
    }

    public static bool ShouldRetry(int consecutiveFailures) =>
        consecutiveFailures < MaximumRecoveryAttempts;

    /// <summary>Bounded, non-decreasing backoff for restart attempt N (1-based).</summary>
    public static TimeSpan BackoffForAttempt(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromMilliseconds(500),
        2 => TimeSpan.FromMilliseconds(1_500),
        _ => TimeSpan.FromMilliseconds(3_000)
    };
}
