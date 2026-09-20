namespace CodexWallpaperSkin;

public enum CapturedInputKind
{
    Down,
    Up,
    Wheel,
    Leave
}

public enum CapturedMouseButton
{
    Left,
    Middle,
    Right
}

/// <summary>
/// One discrete input transition, in the order it happened in Codex. Movement is
/// deliberately not an event: it is carried as state so that a burst of
/// pointer moves coalesces instead of queueing.
/// </summary>
public sealed record CapturedInputEvent(
    CapturedInputKind Kind,
    CapturedMouseButton Button,
    double DeltaY,
    int DeltaMode,
    double X,
    double Y,
    bool Cancelled);

/// <summary>
/// A snapshot of Codex pointer state plus every discrete input event since the
/// previous read.
/// </summary>
public sealed record CapturedPointer(
    double X,
    double Y,
    bool Down,
    bool Hidden,
    IReadOnlyList<CapturedInputEvent> Events,
    bool Overflow)
{
    public static CapturedPointer Empty { get; } = new(0.5, 0.5, false, false, [], false);
}

/// <summary>
/// Maps Codex viewport coordinates onto the Wallpaper Engine render surface.
/// Codex reports CSS pixels normalized to its viewport; the capture surface can
/// have a different size (device scale) and a different aspect ratio, in which
/// case Wallpaper Engine letterboxes the scene. Both steps are pure arithmetic so
/// the mapping is reproducible in tests instead of being inferred from pixels.
/// </summary>
public static class CapturePointerTransform
{
    private const int MaximumWheelDelta = 1_080;

    /// <summary>
    /// Returns the point inside the surface that corresponds to a normalized
    /// viewport position, accounting for device scale and any letterbox.
    /// </summary>
    public static (int X, int Y) MapToSurface(
        double normalizedX,
        double normalizedY,
        int viewportWidth,
        int viewportHeight,
        int surfaceWidth,
        int surfaceHeight)
    {
        var surfaceW = Math.Max(1, surfaceWidth);
        var surfaceH = Math.Max(1, surfaceHeight);
        var viewportW = Math.Max(1, viewportWidth);
        var viewportH = Math.Max(1, viewportHeight);
        var x = Math.Clamp(normalizedX, 0, 1);
        var y = Math.Clamp(normalizedY, 0, 1);

        // The render window is created from the Codex viewport, so the scene
        // occupies the aspect-fitted content rect of the surface. When the aspect
        // ratios match this is the whole surface, exactly as before.
        double contentWidth;
        double contentHeight;
        double offsetX;
        double offsetY;
        var viewportAspect = (double)viewportW / viewportH;
        var surfaceAspect = (double)surfaceW / surfaceH;
        if (surfaceAspect > viewportAspect)
        {
            contentHeight = surfaceH;
            contentWidth = surfaceH * viewportAspect;
            offsetX = (surfaceW - contentWidth) / 2;
            offsetY = 0;
        }
        else
        {
            contentWidth = surfaceW;
            contentHeight = surfaceW / viewportAspect;
            offsetX = 0;
            offsetY = (surfaceH - contentHeight) / 2;
        }

        var mappedX = (int)Math.Round(offsetX + x * contentWidth);
        var mappedY = (int)Math.Round(offsetY + y * contentHeight);
        return (Math.Clamp(mappedX, 0, surfaceW - 1), Math.Clamp(mappedY, 0, surfaceH - 1));
    }

    /// <summary>
    /// Converts a browser wheel delta into the Windows <c>WM_MOUSEWHEEL</c>
    /// rotation. Positive browser <c>deltaY</c> means scrolling down, which is a
    /// negative Windows rotation.
    /// </summary>
    public static int WheelDelta(double deltaY, int deltaMode)
    {
        if (double.IsNaN(deltaY) || double.IsInfinity(deltaY))
        {
            return 0;
        }
        var rotation = deltaMode switch
        {
            1 => -deltaY * 120d,
            2 => -deltaY * 360d,
            _ => -deltaY * 120d / 100d
        };
        return (int)Math.Clamp(Math.Round(rotation), -MaximumWheelDelta, MaximumWheelDelta);
    }
}
