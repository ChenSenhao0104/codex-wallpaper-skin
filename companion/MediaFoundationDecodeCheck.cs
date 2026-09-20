using System.Runtime.InteropServices;

namespace CodexWallpaperSkin;

/// <summary>Result of decoding a produced stream with Media Foundation's own reader.</summary>
internal sealed record MediaDecodeReport(
    int Frames,
    int Width,
    int Height,
    double MediaSeconds,
    long DecodedBytes,
    bool OrientationVerified,
    string Detail)
{
    public bool Passed => Frames > 0 && OrientationVerified;

    public string Describe(bool orientationExpected) =>
        Passed
            ? $"PASS Media Foundation decode ({Frames} frames, {Width}x{Height}, {MediaSeconds:F2}s of media, "
                + $"{DecodedBytes} decoded bytes{(orientationExpected ? ", orientation ok" : string.Empty)})"
            : $"FAIL Media Foundation decode: {Detail}";
}

/// <summary>
/// Decodes a produced fragmented MP4 with Media Foundation's own reader.
///
/// Two acceptance questions are answered here, both of which a structural box
/// check cannot answer: does a Windows media component actually decode the
/// stream the encoder wrote, and is the frame orientation correct? When the
/// fixture carries a known high-contrast marker in its top-left corner, a decoded
/// frame whose top-left is not the marker proves the RGB stride declaration was
/// wrong.
///
/// The stream is fed from memory through <see cref="MediaFoundationReadStream"/>,
/// so the check never needs file system access.
/// </summary>
internal static class MediaFoundationDecodeCheck
{
    private const int SourceReaderFirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int SourceReaderEndOfStream = 0x2;
    private const int MaximumSamples = 200_000;

    /// <summary>Decodes an in-memory stream. <paramref name="expectedWidth"/> and height are 0 when unknown.</summary>
    internal static bool TryDecodeInMemory(
        byte[] stream,
        int expectedWidth,
        int expectedHeight,
        bool orientationExpected,
        out string summary,
        out string failure)
    {
        summary = string.Empty;
        if (!MediaFoundationInterop.TryStartup(out failure))
        {
            return false;
        }
        using var source = new MediaFoundationReadStream(stream);
        var report = Decode(source.InterfacePointer, expectedWidth, expectedHeight, orientationExpected, out failure);
        if (report is null)
        {
            return false;
        }
        summary = report.Describe(orientationExpected);
        return report.Passed;
    }

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
        var report = DecodeFile(path, out failure);
        if (report is null)
        {
            return false;
        }
        summary = report.Describe(orientationExpected: false);
        return report.Passed;
    }

    private static MediaDecodeReport? DecodeFile(string path, out string failure)
    {
        IntPtr reader = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            var hr = MediaFoundationInterop.MFCreateAttributes(out attributes, 2);
            if (MediaFoundationInterop.Succeeded(hr))
            {
                MediaFoundationInterop.SetAttributeUInt32(
                    attributes, MediaFoundationInterop.SourceReaderEnableVideoProcessing, 1);
            }
            hr = MediaFoundationInterop.MFCreateSourceReaderFromURL(path, attributes, out reader);
            if (!MediaFoundationInterop.Succeeded(hr) || reader == IntPtr.Zero)
            {
                failure = $"Media Foundation could not open the produced stream (0x{hr:X8}).";
                return null;
            }
            return ReadAll(reader, 0, 0, false, out failure);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return null;
        }
        finally
        {
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
            if (reader != IntPtr.Zero) Marshal.Release(reader);
        }
    }

    private static MediaDecodeReport? Decode(
        IntPtr byteStream,
        int expectedWidth,
        int expectedHeight,
        bool orientationExpected,
        out string failure)
    {
        IntPtr reader = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            var hr = MediaFoundationInterop.MFCreateAttributes(out attributes, 2);
            if (MediaFoundationInterop.Succeeded(hr))
            {
                MediaFoundationInterop.SetAttributeUInt32(
                    attributes, MediaFoundationInterop.SourceReaderEnableVideoProcessing, 1);
            }
            hr = MediaFoundationInterop.MFCreateSourceReaderFromByteStream(byteStream, attributes, out reader);
            if (!MediaFoundationInterop.Succeeded(hr) || reader == IntPtr.Zero)
            {
                failure = $"Media Foundation could not open the in-memory stream (0x{hr:X8}).";
                return null;
            }
            return ReadAll(reader, expectedWidth, expectedHeight, orientationExpected, out failure);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return null;
        }
        finally
        {
            if (attributes != IntPtr.Zero) Marshal.Release(attributes);
            if (reader != IntPtr.Zero) Marshal.Release(reader);
        }
    }

    private static MediaDecodeReport? ReadAll(
        IntPtr reader,
        int expectedWidth,
        int expectedHeight,
        bool orientationExpected,
        out string failure)
    {
        failure = string.Empty;
        IntPtr rgb32 = IntPtr.Zero;
        try
        {
            var hr = MediaFoundationInterop.MFCreateMediaType(out rgb32);
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                failure = $"The decode output media type could not be created (0x{hr:X8}).";
                return null;
            }
            MediaFoundationInterop.SetAttributeGuid(rgb32, MediaFoundationInterop.MajorType, MediaFoundationInterop.MediaTypeVideo);
            MediaFoundationInterop.SetAttributeGuid(rgb32, MediaFoundationInterop.Subtype, MediaFoundationInterop.VideoFormatRgb32);
            hr = MediaFoundationInterop.SetSourceReaderMediaType(reader, SourceReaderFirstVideoStream, rgb32);
            if (!MediaFoundationInterop.Succeeded(hr))
            {
                failure = $"Media Foundation refused to decode to RGB32 (0x{hr:X8}).";
                return null;
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
                    return null;
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
                        return null;
                    }
                    try
                    {
                        hr = MediaFoundationInterop.LockBuffer(buffer, out var data, out _, out var currentLength);
                        if (!MediaFoundationInterop.Succeeded(hr) || data == IntPtr.Zero)
                        {
                            failure = $"A decoded frame could not be read (0x{hr:X8}).";
                            return null;
                        }
                        try
                        {
                            if (firstFrame is null && currentLength > 0)
                            {
                                var length = checked((int)currentLength);
                                firstFrame = new byte[length];
                                Marshal.Copy(data, firstFrame, 0, length);
                                (width, height) = ResolveGeometry(length, expectedWidth, expectedHeight);
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
                return null;
            }
            var orientationVerified = true;
            if (orientationExpected && !TryCheckTopLeftMarker(firstFrame, width, height, out var detail))
            {
                orientationVerified = false;
                failure = "The decoded frame orientation was wrong: " + detail;
            }
            var seconds = firstTimestamp < 0 ? 0 : (lastTimestamp - firstTimestamp) / 10_000_000d;
            return new MediaDecodeReport(samples, width, height, seconds, bytes, orientationVerified, failure);
        }
        finally
        {
            if (rgb32 != IntPtr.Zero) Marshal.Release(rgb32);
        }
    }

    /// <summary>Uses the caller's geometry when known, otherwise infers it from the decoded buffer length.</summary>
    private static (int Width, int Height) ResolveGeometry(int length, int expectedWidth, int expectedHeight)
    {
        if (expectedWidth > 0 && expectedHeight > 0 && length >= expectedWidth * expectedHeight * 4)
        {
            return (expectedWidth, expectedHeight);
        }
        foreach (var (width, height) in new[] { (1280, 720), (1920, 1080), (960, 540), (640, 360) })
        {
            if (length >= width * height * 4)
            {
                return (width, height);
            }
        }
        var pixels = Math.Max(1, length / 4);
        var inferred = Math.Max(2, (int)Math.Round(Math.Sqrt(pixels * 16d / 9d)));
        var inferredHeight = Math.Max(2, pixels / Math.Max(1, inferred));
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
