using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace CodexWallpaperSkin;

public sealed record H264EncoderCandidate(
    string Name,
    bool Hardware,
    bool Asynchronous,
    string? HardwareUrl);

public sealed record H264EncoderProbeReport(
    bool MediaFoundationStarted,
    IReadOnlyList<H264EncoderCandidate> Encoders,
    string? Error);

/// <summary>
/// Read-only probe for Windows' built-in H.264 Media Foundation transforms.
/// It does not activate an encoder, capture a window, or change application
/// state. The result is also used by Doctor before selecting a v0.4 backend.
/// </summary>
internal static class MediaFoundationH264Probe
{
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;

    public static H264EncoderProbeReport Run()
    {
        var started = false;
        try
        {
            MediaFactory.MFStartup().CheckError();
            started = true;
            var input = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.NV12
            };
            var output = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.H264
            };
            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                MftEnumFlagHardware | MftEnumFlagSortAndFilter,
                input,
                output,
                out var activationPointers,
                out var activationCount);
            var candidates = new List<H264EncoderCandidate>();
            try
            {
                for (var index = 0u; index < activationCount; index++)
                {
                    var pointer = Marshal.ReadIntPtr(activationPointers, checked((int)(index * (uint)IntPtr.Size)));
                    using var activation = new IMFActivate(pointer);
                    var name = ReadString(activation, TransformAttributeKeys.MftFriendlyNameAttribute)
                        ?? "Windows H.264 encoder";
                    var hardwareUrl = ReadString(activation, TransformAttributeKeys.MftEnumHardwareUrlAttribute);
                    candidates.Add(new H264EncoderCandidate(
                        name,
                        Hardware: !string.IsNullOrWhiteSpace(hardwareUrl),
                        Asynchronous: ReadBoolean(activation, TransformAttributeKeys.TransformAsync),
                        hardwareUrl));
                }
            }
            finally
            {
                if (activationPointers != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(activationPointers);
                }
            }
            return new H264EncoderProbeReport(true, candidates, null);
        }
        catch (Exception exception)
        {
            return new H264EncoderProbeReport(started, [], Limit(exception.Message));
        }
        finally
        {
            if (started)
            {
                try { MediaFactory.MFShutdown().CheckError(); } catch { }
            }
        }
    }

    private static string? ReadString(IMFAttributes attributes, Guid key)
    {
        try { return attributes.GetString(key); }
        catch { return null; }
    }

    private static bool ReadBoolean(IMFAttributes attributes, Guid key)
    {
        try { return attributes.GetUInt32(key) != 0; }
        catch { return false; }
    }

    private static string Limit(string message) =>
        message.Length <= 500 ? message : message[..500];
}
