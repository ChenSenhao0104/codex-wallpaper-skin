namespace CodexWallpaperSkin;

/// <summary>Result of inspecting one captured native surface.</summary>
public sealed record FrameQuality(int SampleCount, double MeanLuminance, int LuminanceSpread, string Reason)
{
    public bool Acceptable => Reason.Length == 0;
}

/// <summary>
/// Rejects empty and transient captures before they are ever transported to
/// Codex. Wallpaper Engine returns a blank or uniform surface while its render
/// window is still initializing; publishing that surface is what produces the
/// reported gray/black flashes. The thresholds are deliberately conservative so
/// that legitimately dark or low-contrast scenes are still accepted.
/// </summary>
public static class FrameQualityEvaluator
{
    /// <summary>Fewer samples than this cannot describe a real surface.</summary>
    public const int MinimumSamples = 64;

    /// <summary>
    /// Minimum difference between the darkest and brightest sampled pixel. A
    /// blank GDI surface is bit-identical everywhere, so this only rejects
    /// essentially uniform fills.
    /// </summary>
    public const int MinimumLuminanceSpread = 4;

    /// <summary>
    /// Evaluates 32-bit BGRA pixels. Rows must be tightly packed, which is how
    /// the capture session copies its sampled rows.
    /// </summary>
    public static FrameQuality Evaluate(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length < MinimumSamples * 4)
        {
            return new FrameQuality(0, 0, 0, "The captured surface was too small to trust.");
        }

        var samples = 0;
        var sum = 0d;
        byte minimum = 255;
        byte maximum = 0;
        for (var offset = 0; offset + 2 < bgra.Length; offset += 4)
        {
            var luminance = (byte)((bgra[offset + 2] * 30 + bgra[offset + 1] * 59 + bgra[offset] * 11) / 100);
            sum += luminance;
            if (luminance < minimum) minimum = luminance;
            if (luminance > maximum) maximum = luminance;
            samples++;
        }

        var spread = maximum - minimum;
        var mean = sum / samples;
        if (spread < MinimumLuminanceSpread)
        {
            return new FrameQuality(
                samples,
                mean,
                spread,
                minimum <= 8
                    ? "The captured surface was empty or black."
                    : "The captured surface was a uniform fill.");
        }
        return new FrameQuality(samples, mean, spread, string.Empty);
    }
}
