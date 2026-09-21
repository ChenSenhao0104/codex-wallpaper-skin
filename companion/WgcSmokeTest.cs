using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

internal static class WgcSmokeTest
{
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsVisible = 0x10000000;

    public static async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var window = CreateWindowEx(
            0,
            "STATIC",
            "Codex Wallpaper Skin WGC smoke test",
            WsPopup | WsCaption | WsVisible,
            -30000,
            -30000,
            640,
            360,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The WGC smoke test could not create its private native window.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            ShowWindow(window, 8);
            UpdateWindow(window);
            await using var source = WindowsGraphicsCaptureSource.TryStart(window)
                ?? throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable on this system.");
            var frame = await source.ReadFrameAsync(cancellationToken);
            if (frame.Width < 600 || frame.Height < 300)
            {
                throw new InvalidDataException(
                    $"WGC returned an implausible frame size ({frame.Width}x{frame.Height}).");
            }
            return $"PASS WGC/D3D11 window capture ({frame.Width}x{frame.Height}, BGRA32, {frame.Pixels.Length} bytes)";
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
