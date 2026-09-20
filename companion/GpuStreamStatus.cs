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

    /// <summary>Lowest cadence still reported as a GPU mode rather than as a failure.</summary>
    public const int MinimumGpuFrameRate = 10;

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

    /// <summary>True when a stream runs below the mode the user asked for.</summary>
    public static bool IsBelowRequested(int effectiveFrameRate, int requestedFrameRate) =>
        effectiveFrameRate < NormalizeFrameRate(requestedFrameRate);

    /// <summary>
    /// Chooses the frame rate to declare to the encoder from the measured capture
    /// cadence, never above what the user asked for.
    ///
    /// The declared rate is authoritative — Media Foundation resamples the media
    /// timeline to it — so it must be one the source can actually sustain.
    /// Declaring more makes the encoder repeat frames and wastes bitrate; declaring
    /// far less makes it discard frames the machine could have delivered. The
    /// measured cadence is declared with a small downward margin so ordinary jitter
    /// does not turn into duplication.
    /// </summary>
    public static int AlignFrameRate(int measuredFrameRate, int requestedFrameRate)
    {
        var requested = NormalizeFrameRate(requestedFrameRate);
        if (measuredFrameRate <= 0)
        {
            return requested;
        }
        // Stay clearly under the measurement. The source rate varies over a session
        // (a Scene that slows down must not be padded), and when the declaration is
        // wrong, dropping frames is cheaper and smoother than repeating them.
        var conservative = (int)Math.Floor(measuredFrameRate * 0.85);
        return Math.Clamp(conservative, MinimumGpuFrameRate, requested);
    }

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
