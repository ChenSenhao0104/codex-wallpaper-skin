using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

/// <summary>
/// Decodes a produced fragmented MP4 with Media Foundation's own reader and
/// reports what came back.
///
/// Two acceptance questions are answered here, both of which a structural box
/// check cannot answer: does a Windows media component actually decode the
/// stream the encoder wrote, and is the frame orientation correct? The fixture
/// carries a known high-contrast marker in its top-left corner, so a decoded
/// frame whose top-left is not the marker proves the RGB stride declaration was
/// wrong.
/// </summary>
internal static class MediaFoundationDecodeCheck
{
    private const int SourceReaderFirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int SourceReaderEndOfStream = 0x2;
    private const int MaximumSamples = 200_000;

    internal static bool TryDecode(string path, out string summary, out string failure)
    {
        summary = string.Empty;
        failure = string.Empty;
        if (!File.Exists(path))
        {
            failure = "The decode check needs an existing media file.";
            return false;
        }
        if (!MediaFoundationInterop.TryStartup(out failure))
        {
            return false;
        }

        IntPtr reader = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        IntPtr rgb32 = IntPtr.Zero;
        try
        {
            var hr = MediaFoundationInterop.MFCreateAttributes(out attributes, 2);
            if (MediaFoundationInterop.Succeeded(hr))
            {
                MediaFoundationInterop.SetAttributeUInt32(attributes, MediaFoundationInterop.SourceReaderEnableVideoProcessing, 1);
            }
            hr = MediaFoundationInterop.MFCreateSourceReaderFromURL(path, attributes, out reader);
            if (!MediaFoundationInterop.Succeeded(hr) || reader == IntPtr.Zero)
            {
                failure = $"Media Foundation could not open the produced stream (0x{hr:X8}).";
                return false;
            }

            hr = MediaFoundationInterop.MFCreateMediaType(out rgb32);
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                failure = $"The decode output media type could not be created (0x{hr:X8}).";
                return false;
            }
            MediaFoundationInterop.SetAttributeGuid(rgb32, MediaFoundationInterop.MajorType, MediaFoundationInterop.MediaTypeVideo);
            MediaFoundationInterop.SetAttributeGuid(rgb32, MediaFoundationInterop.Subtype, MediaFoundationInterop.VideoFormatRgb32);
            hr = MediaFoundationInterop.SetSourceReaderMediaType(reader, SourceReaderFirstVideoStream, rgb32);
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                failure = $"Media Foundation refused to decode to RGB32 (0x{hr:X8}).";
                return false;
            }

            var samples = 0;
            var bytes = 0L;
            long firstTimestamp = -1;
            long lastTimestamp = 0;
            byte[]? firstFrame = null;
            var width = 0;
            var height = 0;

            while (samples < MaximumSamples)
            {
                hr = MediaFoundationInterop.ReadSample(
                    reader, SourceReaderFirstVideoStream, 0,
                    out _, out var streamFlags, out var timestamp, out var sample);
                if (!MediaFoundationInterop.Succeeded(hr))
                {
                    failure = $"Media Foundation failed while decoding sample {samples} (0x{hr:X8}).";
                    return false;
                }
                if ((streamFlags & SourceReaderEndOfStream) != 0)
                {
                    break;
                }
                if (sample == IntPtr.Zero)
                {
                    continue;
                }
                try
                {
                    hr = MediaFoundationInterop.ConvertSampleToBuffer(sample, out var buffer);
                    if (!MediaFoundationInterop.Succeeded(hr) || buffer == IntPtr.Zero)
                    {
                        failure = $"A decoded sample had no contiguous buffer (0x{hr:X8}).";
                        return false;
                    }
                    try
                    {
                        hr = MediaFoundationInterop.LockBuffer(buffer, out var data, out var maximumLength, out var currentLength);
                        if (!MediaFoundationInterop.Succeeded(hr) || data == IntPtr.Zero)
                        {
                            failure = $"A decoded frame could not be read (0x{hr:X8}).";
                            return false;
                        }
                        try
                        {
                            if (firstFrame is null && currentLength > 0)
                            {
                                // 1280x720x4 is the fixture geometry; the decoder is
                                // asked for RGB32 so the buffer is tightly packed.
                                var length = checked((int)currentLength);
                                firstFrame = new byte[length];
                                Marshal.Copy(data, firstFrame, 0, length);
                                (width, height) = EstimateGeometry(length);
                            }
                        }
                        finally
                        {
                            MediaFoundationInterop.UnlockBuffer(buffer);
                        }
                        bytes += currentLength;
                    }
                    finally
                    {
                        Marshal.Release(buffer);
                    }
                    if (firstTimestamp < 0)
                    {
                        firstTimestamp = timestamp;
                    }
                    lastTimestamp = timestamp;
                    samples++;
                }
                finally
                {
                    Marshal.Release(sample);
                }
            }

            if (samples == 0 || firstFrame is null)
            {
                failure = "Media Foundation decoded no frames from the produced stream.";
                return false;
            }
            if (!TryCheckTopLeftMarker(firstFrame, width, height, out var markerDetail))
            {
                failure = "The decoded frame orientation was wrong: " + markerDetail;
                return false;
            }

            var seconds = firstTimestamp < 0 ? 0 : (lastTimestamp - firstTimestamp) / 10_000_000d;
            summary = $"PASS Media Foundation decode ({samples} frames, {width}x{height}, "
                + $"{seconds:F2}s of media, {bytes} decoded bytes, orientation ok)";
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
        finally
        {
            if (rgb32 != IntPtr.Zero) Marshal.Release(rgb32);
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
            if (reader != IntPtr.Zero) Marshal.Release(reader);
        }
    }

    /// <summary>Infers a 16:9 geometry from the decoded RGB32 buffer length.</summary>
    private static (int Width, int Height) EstimateGeometry(int length)
    {
        foreach (var (width, height) in new[] { (1280, 720), (1920, 1080), (960, 540), (640, 360) })
        {
            if (length >= width * height * 4)
            {
                return (width, height);
            }
        }
        var pixels = Math.Max(1, length / 4);
        var inferred = (int)Math.Round(Math.Sqrt(pixels * 16d / 9d));
        var inferredHeight = Math.Max(1, pixels / Math.Max(1, inferred));
        return (inferred, inferredHeight);
    }

    /// <summary>
    /// The synthetic fixture paints a strong red marker in its top-left corner and
    /// a blue/green gradient elsewhere, so this detects a vertically flipped or
    /// channel-swapped decode.
    /// </summary>
    private static bool TryCheckTopLeftMarker(byte[] bgra, int width, int height, out string detail)
    {
        detail = string.Empty;
        if (width < 64 || height < 64 || bgra.Length < width * height * 4)
        {
            detail = "the decoded buffer was smaller than the expected frame.";
            return false;
        }
        var marker = Math.Min(48, Math.Min(width, height));
        double cornerRed = 0, cornerBlue = 0, cornerGreen = 0, oppositeRed = 0;
        var samples = 0;
        for (var y = 4; y < marker - 4; y += 4)
        {
            for (var x = 4; x < marker - 4; x += 4)
            {
                var top = ((y * width) + x) * 4;
                cornerBlue += bgra[top];
                cornerGreen += bgra[top + 1];
                cornerRed += bgra[top + 2];
                var bottom = (((height - 1 - y) * width) + (width - 1 - x)) * 4;
                oppositeRed += bgra[bottom + 2];
                samples++;
            }
        }
        if (samples == 0)
        {
            detail = "the marker region could not be sampled.";
            return false;
        }
        cornerRed /= samples;
        cornerGreen /= samples;
        cornerBlue /= samples;
        oppositeRed /= samples;
        if (cornerRed < 180 || cornerGreen > 90 || cornerBlue > 90)
        {
            detail = $"the top-left corner was not the expected red marker "
                + $"(r={cornerRed:F0} g={cornerGreen:F0} b={cornerBlue:F0}); "
                + "a vertically flipped or channel-swapped decode produces this.";
            return false;
        }
        if (oppositeRed >= cornerRed)
        {
            detail = "the opposite corner was as red as the marker, so the frame is not the expected geometry.";
            return false;
        }
        return true;
    }
}
