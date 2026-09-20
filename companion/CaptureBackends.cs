using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

/// <summary>How native Wallpaper Engine frames are captured.</summary>
public enum CaptureBackend
{
    None,
    WindowsGraphicsCapture,
    PrintWindow
}

/// <summary>
/// What this machine can do, and why. Section 3 prefers Windows Graphics Capture
/// backed by D3D11 and allows a documented fallback, so the fallback has to be
/// named and justified at runtime instead of being implicit.
/// </summary>
public sealed record CaptureBackendAvailability(
    bool IsWindows,
    int OsBuild,
    bool GraphicsCaptureActivatable,
    string Reason)
{
    public bool WindowsGraphicsCaptureSupported => IsWindows && GraphicsCaptureActivatable;
}

public static class CaptureBackends
{
    /// <summary>Windows 10 1903 is the first release with window capture.</summary>
    public const int MinimumGraphicsCaptureBuild = 18362;

    /// <summary>
    /// The path this build implements for production capture. Windows Graphics
    /// Capture is preferred by the specification but is not implemented yet, so
    /// the PrintWindow fallback is used and reported as such.
    /// </summary>
    public const CaptureBackend ImplementedCaptureBackend = CaptureBackend.PrintWindow;

    private static readonly Lazy<CaptureBackendAvailability> CachedAvailability =
        new(Detect, isThreadSafe: true);

    /// <summary>Probed once per process; the probe activates a WinRT class.</summary>
    public static CaptureBackendAvailability Availability => CachedAvailability.Value;

    public static CaptureBackendAvailability Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CaptureBackendAvailability(false, 0, false, "this platform is not Windows");
        }
        var build = Environment.OSVersion.Version.Build;
        if (build < MinimumGraphicsCaptureBuild)
        {
            return new CaptureBackendAvailability(
                true,
                build,
                false,
                $"Windows build {build} predates window capture (requires build {MinimumGraphicsCaptureBuild} or newer)");
        }

        var activatable = TryActivateGraphicsCaptureSessionFactory(out var error);
        return activatable
            ? new CaptureBackendAvailability(
                true, build, true, $"Windows Graphics Capture activates on Windows build {build}")
            : new CaptureBackendAvailability(
                true, build, false, $"Windows Graphics Capture could not be activated: {error}");
    }

    /// <summary>Chooses the capture path. Never silent: the caller reports it.</summary>
    public static CaptureBackend Select(CaptureBackendAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        return ImplementedCaptureBackend;
    }

    public static string BackendName(CaptureBackend backend) => backend switch
    {
        CaptureBackend.WindowsGraphicsCapture => "Windows Graphics Capture (D3D11)",
        CaptureBackend.PrintWindow => "PrintWindow on a private play-in-window surface",
        _ => "no capture backend"
    };

    /// <summary>One concise sentence for the product status and Doctor.</summary>
    public static string Describe(CaptureBackendAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var name = BackendName(Select(availability));
        if (!availability.IsWindows)
        {
            return $"{name} (documented fallback; {availability.Reason})";
        }
        if (!availability.WindowsGraphicsCaptureSupported)
        {
            return $"{name} (documented fallback; Windows Graphics Capture unavailable because {availability.Reason})";
        }
        return $"{name} (documented fallback; Windows Graphics Capture is available on this system "
            + "but is not implemented by this build yet)";
    }

    private const string GraphicsCaptureSessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";

    /// <summary>IID_IActivationFactory.</summary>
    private static readonly Guid ActivationFactoryIid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    /// <summary>
    /// Probes the Windows Runtime on a dedicated MTA thread so the WPF STA thread
    /// is untouched, and so a failed activation cannot disturb COM state that the
    /// renderer depends on. Only P/Invoke is used: this build cannot reference the
    /// Windows SDK projection while NuGet is unavailable.
    /// </summary>
    private static bool TryActivateGraphicsCaptureSessionFactory(out string error)
    {
        var failure = string.Empty;
        var succeeded = false;
        var thread = new Thread(() =>
        {
            IntPtr hstring = IntPtr.Zero;
            IntPtr factory = IntPtr.Zero;
            var initialized = RoInitialize(RoInitMultiThreaded);
            try
            {
                var hr = WindowsCreateString(GraphicsCaptureSessionClass, GraphicsCaptureSessionClass.Length, out hstring);
                if (hr < 0)
                {
                    failure = $"the class name could not be created (0x{hr:X8})";
                    return;
                }
                var iid = ActivationFactoryIid;
                hr = RoGetActivationFactory(hstring, ref iid, out factory);
                if (hr < 0 || factory == IntPtr.Zero)
                {
                    failure = $"the Windows Runtime rejected activation (0x{hr:X8})";
                    return;
                }
                succeeded = true;
            }
            catch (DllNotFoundException)
            {
                failure = "the Windows Runtime is unavailable";
            }
            catch (EntryPointNotFoundException)
            {
                failure = "the Windows Runtime is unavailable";
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name;
            }
            finally
            {
                if (factory != IntPtr.Zero)
                {
                    try { Marshal.Release(factory); } catch { }
                }
                if (hstring != IntPtr.Zero)
                {
                    try { WindowsDeleteString(hstring); } catch { }
                }
                if (initialized >= 0)
                {
                    try { RoUninitialize(); } catch { }
                }
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.MTA);
        try
        {
            thread.Start();
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name;
            return false;
        }
        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            error = "the probe did not finish within 5 seconds";
            return false;
        }
        error = failure;
        return succeeded;
    }

    private const int RoInitMultiThreaded = 1;

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern void RoUninitialize();
}
