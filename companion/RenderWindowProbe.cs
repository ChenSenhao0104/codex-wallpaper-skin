using System.Runtime.InteropServices;
using System.Text;

namespace CodexWallpaperSkin;

/// <summary>
/// Inspects top-level windows by title. Gate 8 requires Restore to leave no
/// owned render window behind, and "the session object says it is disposed" is
/// not the same claim as "the window is gone from this desktop" — this probe
/// checks the desktop itself.
/// </summary>
public static class RenderWindowProbe
{
    /// <summary>The title prefix every private render window this app creates uses.</summary>
    public const string WindowTitlePrefix = "Codex Wallpaper Skin ";

    public static bool IsWindowPresent(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }
        return FindWindow(null, title) != IntPtr.Zero;
    }

    /// <summary>
    /// Asks a window to close and waits for it to disappear. This is the backstop
    /// for an owned render window whose control-channel close silently failed,
    /// which is how a stale window would otherwise survive Restore.
    /// </summary>
    public static bool TryCloseWindow(string title, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(title) || !OperatingSystem.IsWindows())
        {
            return true;
        }
        var handle = FindWindow(null, title);
        if (handle == IntPtr.Zero)
        {
            return true;
        }
        if (!PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero))
        {
            return false;
        }
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (FindWindow(null, title) == IntPtr.Zero)
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return FindWindow(null, title) == IntPtr.Zero;
    }

    /// <summary>An open top-level window that carries this app's render title.</summary>
    public sealed record OpenWindow(IntPtr Handle, string Title, int ProcessId);

    /// <summary>
    /// Lists open windows whose title starts with the prefix, with the owning
    /// process, so a leftover render window can be attributed before it is closed.
    /// </summary>
    public static IReadOnlyList<OpenWindow> FindOpenWindows(string titlePrefix)
    {
        var found = new List<OpenWindow>();
        if (string.IsNullOrWhiteSpace(titlePrefix) || !OperatingSystem.IsWindows())
        {
            return found;
        }
        var buffer = new StringBuilder(512);
        try
        {
            EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle))
                {
                    return true;
                }
                buffer.Clear();
                if (GetWindowTextW(handle, buffer, buffer.Capacity) <= 0)
                {
                    return true;
                }
                var title = buffer.ToString();
                if (!title.StartsWith(titlePrefix, StringComparison.Ordinal))
                {
                    return true;
                }
                GetWindowThreadProcessId(handle, out var processId);
                found.Add(new OpenWindow(handle, title, checked((int)processId)));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // An enumeration failure must never break capture.
        }
        return found;
    }

    /// <summary>Posts a close request to a known window and waits for it to vanish.</summary>
    public static bool TryCloseWindowHandle(IntPtr handle, TimeSpan timeout)
    {
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindows())
        {
            return true;
        }
        if (!PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero))
        {
            return false;
        }
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsWindow(handle))
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return !IsWindow(handle);
    }

    private const uint WmClose = 0x0010;

    /// <summary>Counts open top-level windows whose title starts with the prefix.</summary>
    public static int CountOpenWindows(string titlePrefix)
    {
        if (string.IsNullOrWhiteSpace(titlePrefix) || !OperatingSystem.IsWindows())
        {
            return 0;
        }
        var count = 0;
        var buffer = new StringBuilder(512);
        try
        {
            EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle))
                {
                    return true;
                }
                buffer.Clear();
                var length = GetWindowTextW(handle, buffer, buffer.Capacity);
                if (length > 0 && buffer.ToString().StartsWith(titlePrefix, StringComparison.Ordinal))
                {
                    count++;
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            return count;
        }
        return count;
    }

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextW(IntPtr handle, StringBuilder text, int maximumCount);
}
