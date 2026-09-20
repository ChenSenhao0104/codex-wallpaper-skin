namespace CodexWallpaperSkin;

/// <summary>
/// Product-facing v0.4 stream states. Every value maps to one of the status
/// labels required by Issue #2 so Doctor, the controller window and the
/// acceptance report describe the same condition with the same words.
/// </summary>
public enum GpuStreamStatus
{
    GpuDynamic60,
    GpuDynamic30Fallback,
    ReducedFrameRateCompatibility,
    StaticPreviewFallback,
    Recovering,
    UnsupportedOrFailed
}

public static class GpuStreamStatusLabel
{
    public const string GpuDynamic60 = "GPU dynamic - 60 FPS target";
    public const string GpuDynamic30Fallback = "GPU dynamic - 30 FPS fallback";
    public const string ReducedFrameRateCompatibility = "reduced-frame-rate JPEG compatibility";
    public const string StaticPreviewFallback = "static/preview fallback";
    public const string Recovering = "recovering";
    public const string UnsupportedOrFailed = "unsupported or failed";

    /// <summary>Frame-rate modes the product offers. 60 is the target, 30 is an explicit fallback.</summary>
    public const int TargetFrameRate = 60;
    public const int FallbackFrameRate = 30;

    public static string Describe(GpuStreamStatus status) => status switch
    {
        GpuStreamStatus.GpuDynamic60 => GpuDynamic60,
        GpuStreamStatus.GpuDynamic30Fallback => GpuDynamic30Fallback,
        GpuStreamStatus.ReducedFrameRateCompatibility => ReducedFrameRateCompatibility,
        GpuStreamStatus.StaticPreviewFallback => StaticPreviewFallback,
        GpuStreamStatus.Recovering => Recovering,
        _ => UnsupportedOrFailed
    };

    public static bool IsGpuDynamic(GpuStreamStatus status) =>
        status is GpuStreamStatus.GpuDynamic60 or GpuStreamStatus.GpuDynamic30Fallback;

    /// <summary>
    /// True when a GPU mode is running below the 30 FPS fallback floor, which
    /// Issue #2 requires to be diagnosed as degraded instead of reported as
    /// success. Kept as a pure function so the threshold is unit tested directly
    /// rather than only through a five-second run.
    /// </summary>
    public static bool IsDegradedCadence(GpuStreamStatus status, double observedSeconds, double presentedFps) =>
        IsGpuDynamic(status)
        && observedSeconds >= 5
        && presentedFps < FallbackFrameRate - 2;

    /// <summary>True when the stream is being run below the mode the user asked for.</summary>
    public static bool IsBelowRequested(int declaredFrameRate, int requestedFrameRate) =>
        declaredFrameRate < NormalizeFrameRate(requestedFrameRate);

    /// <summary>
    /// Maps a stored or reported label back to its state so a persisted status
    /// from another build cannot silently claim a GPU state it never reached.
    /// </summary>
    public static bool TryParse(string? label, out GpuStreamStatus status)
    {
        foreach (var candidate in Enum.GetValues<GpuStreamStatus>())
        {
            if (string.Equals(Describe(candidate), label, StringComparison.Ordinal)
                || string.Equals(candidate.ToString(), label, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }
        status = GpuStreamStatus.UnsupportedOrFailed;
        return false;
    }

    /// <summary>
    /// Normalizes a requested frame rate onto the two supported modes. Anything
    /// that is not an explicit 60 FPS request is a labeled 30 FPS fallback.
    /// </summary>
    public static int NormalizeFrameRate(int requested) =>
        requested >= TargetFrameRate ? TargetFrameRate : FallbackFrameRate;

    /// <summary>
    /// Chooses the frame rate to declare to the encoder from the measured capture
    /// cadence, never above what the user asked for.
    ///
    /// The measured cadence itself is declared rather than snapping to 30 or 60.
    /// Declaring a higher rate makes Media Foundation repeat frames so the product
    /// can claim a cadence the user cannot see; declaring a lower one makes the
    /// encoder discard frames it was fast enough to accept. Declaring what was
    /// measured keeps the media timeline equal to real time in both directions.
    /// </summary>
    public static int AlignFrameRate(int measuredFrameRate, int requestedFrameRate)
    {
        var requested = NormalizeFrameRate(requestedFrameRate);
        if (measuredFrameRate <= 0)
        {
            return requested;
        }
        return Math.Clamp(measuredFrameRate, MinimumGpuFrameRate, requested);
    }

    /// <summary>Lowest cadence still reported as a GPU mode rather than as a failure.</summary>
    public const int MinimumGpuFrameRate = 10;

    /// <summary>
    /// Selects the honest product status. The order matters: an active recovery
    /// is reported as recovering rather than as success, and a failed GPU path
    /// never borrows a GPU label from the compatibility backend.
    /// </summary>
    public static GpuStreamStatus Decide(GpuStreamStatusInput input)
    {
        if (input.Recovering)
        {
            return GpuStreamStatus.Recovering;
        }
        if (input.GpuPathActive)
        {
            return input.RequestedFrameRate >= TargetFrameRate
                ? GpuStreamStatus.GpuDynamic60
                : GpuStreamStatus.GpuDynamic30Fallback;
        }
        if (input.CompatibilityCaptureAvailable)
        {
            return GpuStreamStatus.ReducedFrameRateCompatibility;
        }
        if (input.StaticFallbackAvailable)
        {
            return GpuStreamStatus.StaticPreviewFallback;
        }
        return GpuStreamStatus.UnsupportedOrFailed;
    }
}

/// <summary>Inputs to <see cref="GpuStreamStatusLabel.Decide"/>; kept as a record so tests can drive every branch.</summary>
public readonly record struct GpuStreamStatusInput(
    bool GpuPathActive,
    bool Recovering,
    int RequestedFrameRate,
    bool CompatibilityCaptureAvailable,
    bool StaticFallbackAvailable);
