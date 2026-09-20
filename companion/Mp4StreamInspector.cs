using System.Buffers.Binary;

namespace CodexWallpaperSkin;

/// <summary>
/// Structural report for one fragmented MP4 byte stream.
/// </summary>
internal sealed record Mp4StreamReport(
    IReadOnlyList<string> TopLevelBoxes,
    bool HasMovieBox,
    bool HasMovieExtendsBox,
    bool HasAvcConfiguration,
    int SampleCount,
    int Width,
    int Height,
    int Timescale,
    long TotalBytes)
{
    /// <summary>
    /// True when the stream satisfies everything Media Source Extensions and the
    /// Chromium media pipeline require before an initialisation segment may be
    /// appended followed by media fragments.
    /// </summary>
    public bool IsMseCompatible => HasMovieBox && HasMovieExtendsBox && HasAvcConfiguration && SampleCount > 0;

    public string Describe() =>
        $"{(IsMseCompatible ? "MSE-compatible" : "NOT MSE-compatible")}: "
        + $"boxes=[{string.Join(",", TopLevelBoxes)}] moov={HasMovieBox} mvex={HasMovieExtendsBox} "
        + $"avcC={HasAvcConfiguration} samples={SampleCount} size={Width}x{Height} timescale={Timescale} bytes={TotalBytes}";
}

/// <summary>Result of validating the AVC elementary stream that was muxed into the media data boxes.</summary>
internal sealed record AvcBitstreamReport(
    int MediaDataBoxes,
    int NalUnits,
    int SequenceParameterSets,
    int PictureParameterSets,
    int InstantaneousRefreshFrames,
    int NonRefreshFrames,
    int NalLengthSize)
{
    public bool IsDecodable =>
        MediaDataBoxes > 0
        && NalUnits > 0
        && SequenceParameterSets > 0
        && PictureParameterSets > 0
        && InstantaneousRefreshFrames > 0
        && NonRefreshFrames > 0
        && NalLengthSize == 4;

    public string Describe() =>
        $"{(IsDecodable ? "AVC bitstream OK" : "AVC bitstream INVALID")}: mdat={MediaDataBoxes} nal={NalUnits} "
        + $"sps={SequenceParameterSets} pps={PictureParameterSets} idr={InstantaneousRefreshFrames} "
        + $"non-idr={NonRefreshFrames} lengthSize={NalLengthSize}";
}

/// <summary>
/// Read-only fragmented MP4 inspection used by the automated tests. It exists so
/// the encoder can be proven to produce a container that Media Source Extensions
/// accepts without needing a GPU, a wallpaper, or a browser in the test path.
/// </summary>
internal static class Mp4StreamInspector
{
    /// <summary>Fixed fields of a VisualSampleEntry that precede its nested boxes such as avcC.</summary>
    private const int VisualSampleEntryBytes = 78;

    /// <summary>
    /// Validates the AVC elementary stream inside the media data boxes.
    ///
    /// This is the check that would catch a stream that is structurally a valid
    /// fragmented MP4 but that no decoder can play: it verifies the declared NAL
    /// length size against the avcC decoder configuration, that every
    /// length-prefixed NAL unit fits inside its media data box, and that
    /// sequence/picture parameter sets and at least one instantaneous refresh
    /// frame are actually present.
    /// </summary>
    public static AvcBitstreamReport InspectAvcBitstream(ReadOnlySpan<byte> stream)
    {
        var mediaDataBoxes = 0;
        var nalUnits = 0;
        var sequenceParameterSets = 0;
        var pictureParameterSets = 0;
        var idrFrames = 0;
        var nonIdrFrames = 0;
        var lengthSize = 0;

        var offset = 0;
        while (offset + 8 <= stream.Length)
        {
            if (!TryReadBox(stream, offset, out var type, out var size, out var headerBytes))
            {
                break;
            }
            var body = stream.Slice(offset + headerBytes, size - headerBytes);
            if (type == "moov"
                && FindBox(body, "trak", out var trak)
                && FindBox(trak, "mdia", out var mdia)
                && FindBox(mdia, "minf", out var minf)
                && FindBox(minf, "stbl", out var stbl)
                && FindBox(stbl, "stsd", out var stsd)
                && stsd.Length > 8
                && FindBox(stsd[8..], "avc1", out var avc1)
                && avc1.Length > VisualSampleEntryBytes
                && FindBox(avc1[VisualSampleEntryBytes..], "avcC", out var avcC))
            {
                lengthSize = ReadAvcConfiguration(avcC, ref sequenceParameterSets, ref pictureParameterSets);
            }
            else if (type == "mdat")
            {
                mediaDataBoxes++;
                var cursor = 0;
                while (cursor + 4 <= body.Length)
                {
                    var nalLength = (int)BinaryPrimitives.ReadUInt32BigEndian(body[cursor..]);
                    if (nalLength <= 0 || cursor + 4 + nalLength > body.Length)
                    {
                        // A NAL unit that does not fit means the media data and the
                        // fragment table disagree, which no decoder can recover from.
                        return new AvcBitstreamReport(
                            mediaDataBoxes, nalUnits, sequenceParameterSets, pictureParameterSets,
                            idrFrames, nonIdrFrames, lengthSize);
                    }
                    var nalType = body[cursor + 4] & 0x1F;
                    nalUnits++;
                    switch (nalType)
                    {
                        case 5: idrFrames++; break;
                        case 1: nonIdrFrames++; break;
                        case 7: sequenceParameterSets++; break;
                        case 8: pictureParameterSets++; break;
                    }
                    cursor += 4 + nalLength;
                }
                if (cursor != body.Length)
                {
                    return new AvcBitstreamReport(
                        mediaDataBoxes, nalUnits, sequenceParameterSets, pictureParameterSets,
                        idrFrames, nonIdrFrames, lengthSize);
                }
            }
            offset += size;
        }

        return new AvcBitstreamReport(
            mediaDataBoxes, nalUnits, sequenceParameterSets, pictureParameterSets,
            idrFrames, nonIdrFrames, lengthSize);
    }

    /// <summary>Reads the avcC decoder configuration: NAL length size plus the parameter set counts.</summary>
    private static int ReadAvcConfiguration(ReadOnlySpan<byte> avcC, ref int sequenceParameterSets, ref int pictureParameterSets)
    {
        if (avcC.Length < 7)
        {
            return 0;
        }
        var lengthSize = (avcC[4] & 0x03) + 1;
        var spsCount = avcC[5] & 0x1F;
        var cursor = 6;
        for (var index = 0; index < spsCount && cursor + 2 <= avcC.Length; index++)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(avcC[cursor..]);
            cursor += 2 + length;
            sequenceParameterSets++;
        }
        if (cursor < avcC.Length)
        {
            var ppsCount = avcC[cursor];
            cursor++;
            for (var index = 0; index < ppsCount && cursor + 2 <= avcC.Length; index++)
            {
                var length = BinaryPrimitives.ReadUInt16BigEndian(avcC[cursor..]);
                cursor += 2 + length;
                pictureParameterSets++;
            }
        }
        return lengthSize;
    }

    public static Mp4StreamReport Inspect(ReadOnlySpan<byte> stream)
    {
        var boxes = new List<string>();
        var hasMoov = false;
        var hasMvex = false;
        var hasAvcC = false;
        var samples = 0;
        var width = 0;
        var height = 0;
        var timescale = 0;

        var offset = 0;
        while (offset + 8 <= stream.Length)
        {
            if (!TryReadBox(stream, offset, out var type, out var size, out var headerBytes))
            {
                break;
            }
            boxes.Add(type);
            var body = stream.Slice(offset + headerBytes, size - headerBytes);
            switch (type)
            {
                case "moov":
                    hasMoov = true;
                    hasMvex = FindBox(body, "mvex", out _);
                    if (FindBox(body, "trak", out var trak))
                    {
                        // trak, mdia, minf and stbl are plain container boxes: their
                        // children start immediately after the eight-byte header.
                        // stsd is a full box, so its entry count must be skipped.
                        if (FindBox(trak, "mdia", out var mdia))
                        {
                            if (timescale == 0 && FindBox(mdia, "mdhd", out var mdhd))
                            {
                                timescale = ReadFullBoxTimescale(mdhd);
                            }
                            if (FindBox(mdia, "minf", out var minf)
                                && FindBox(minf, "stbl", out var stbl)
                                && FindBox(stbl, "stsd", out var stsd)
                                && stsd.Length > 8
                                && FindBox(stsd[8..], "avc1", out var avc1))
                            {
                                // VisualSampleEntry: 6 reserved bytes, data_reference_index,
                                // then 16 bytes of pre-defined fields before width/height,
                                // and 78 bytes in total before nested boxes such as avcC.
                                if (avc1.Length >= 28)
                                {
                                    width = BinaryPrimitives.ReadUInt16BigEndian(avc1[24..]);
                                    height = BinaryPrimitives.ReadUInt16BigEndian(avc1[26..]);
                                }
                                hasAvcC = avc1.Length > VisualSampleEntryBytes
                                    && FindBox(avc1[VisualSampleEntryBytes..], "avcC", out _);
                            }
                        }
                    }
                    break;
                case "moof":
                    if (FindBox(body, "traf", out var traf) && FindBox(traf, "trun", out var trun))
                    {
                        samples += ReadTrunSampleCount(trun);
                    }
                    break;
            }
            offset += size;
        }

        return new Mp4StreamReport(boxes, hasMoov, hasMvex, hasAvcC, samples, width, height, timescale, stream.Length);
    }

    private static bool TryReadBox(ReadOnlySpan<byte> stream, int offset, out string type, out int size, out int headerBytes)
    {
        type = string.Empty;
        size = 0;
        headerBytes = 8;
        if (offset + 8 > stream.Length)
        {
            return false;
        }
        var declared = BinaryPrimitives.ReadUInt32BigEndian(stream[offset..]);
        if (declared == 1)
        {
            if (offset + 16 > stream.Length)
            {
                return false;
            }
            var large = BinaryPrimitives.ReadUInt64BigEndian(stream[(offset + 8)..]);
            if (large > int.MaxValue)
            {
                return false;
            }
            size = (int)large;
            headerBytes = 16;
        }
        else
        {
            if (declared < 8 || declared > int.MaxValue)
            {
                return false;
            }
            size = (int)declared;
        }
        if (size < headerBytes || offset + size > stream.Length)
        {
            return false;
        }
        type = ReadType(stream, offset + 4);
        return true;
    }

    /// <summary>
    /// Builds the exact Media Source Extensions codec string from the avcC decoder
    /// configuration inside an initialisation segment.
    ///
    /// Announcing a guessed profile would be dishonest and can make Chromium
    /// refuse the buffer, so the profile, compatibility and level bytes are read
    /// from the stream the encoder actually produced.
    /// </summary>
    public static bool TryReadAvcCodecString(ReadOnlySpan<byte> initSegment, out string codec)
    {
        codec = string.Empty;
        var offset = 0;
        while (offset + 8 <= initSegment.Length)
        {
            if (!TryReadBox(initSegment, offset, out var type, out var size, out var headerBytes))
            {
                return false;
            }
            if (type == "moov"
                && FindBox(initSegment.Slice(offset + headerBytes, size - headerBytes), "trak", out var trak)
                && FindBox(trak, "mdia", out var mdia)
                && FindBox(mdia, "minf", out var minf)
                && FindBox(minf, "stbl", out var stbl)
                && FindBox(stbl, "stsd", out var stsd)
                && stsd.Length > 8
                && FindBox(stsd[8..], "avc1", out var avc1)
                && avc1.Length > VisualSampleEntryBytes
                && FindBox(avc1[VisualSampleEntryBytes..], "avcC", out var avcC)
                && avcC.Length >= 4)
            {
                codec = $"avc1.{avcC[1]:X2}{avcC[2]:X2}{avcC[3]:X2}";
                return true;
            }
            offset += size;
        }
        return false;
    }

    private static bool FindBox(ReadOnlySpan<byte> container, string type, out ReadOnlySpan<byte> body)
    {
        body = default;
        var offset = 0;
        while (offset + 8 <= container.Length)
        {
            if (!TryReadBox(container, offset, out var candidate, out var size, out var headerBytes))
            {
                return false;
            }
            if (candidate == type)
            {
                body = container.Slice(offset + headerBytes, size - headerBytes);
                return true;
            }
            offset += size;
        }
        return false;
    }

    /// <summary>Reads the sample count from a trun box body that still includes its version and flags.</summary>
    private static int ReadTrunSampleCount(ReadOnlySpan<byte> trun)
    {
        // FullBox: version/flags then sample_count.
        if (trun.Length < 8)
        {
            return 0;
        }
        return (int)BinaryPrimitives.ReadUInt32BigEndian(trun[4..]);
    }

    private static int ReadFullBoxTimescale(ReadOnlySpan<byte> mdhd)
    {
        // FullBox: version(1) flags(3) then version 0: creation(4) modification(4) timescale(4)
        // version 1: creation(8) modification(8) timescale(4).
        if (mdhd.Length < 4)
        {
            return 0;
        }
        var version = mdhd[0];
        var index = version == 1 ? 20 : 12;
        return mdhd.Length >= index + 4 ? (int)BinaryPrimitives.ReadUInt32BigEndian(mdhd[index..]) : 0;
    }

    private static string ReadType(ReadOnlySpan<byte> stream, int offset)
    {
        Span<char> type = stackalloc char[4];
        for (var index = 0; index < 4; index++)
        {
            var value = stream[offset + index];
            type[index] = value is >= 0x20 and < 0x7F ? (char)value : '?';
        }
        return new string(type);
    }
}
