namespace CodexWallpaperSkin;

internal enum Mp4ChunkKind
{
    /// <summary>The initialisation segment (ftyp + moov) that Media Source Extensions requires first.</summary>
    Init,

    /// <summary>One media fragment (moof + mdat).</summary>
    Media
}

/// <summary>One complete, MSE-appendable unit produced by <see cref="Mp4ChunkAssembler"/>.</summary>
internal sealed record Mp4Chunk(
    Mp4ChunkKind Kind,
    byte[] Bytes,
    int Sequence,
    DateTimeOffset CreatedAt,
    long LastSubmittedFrameTicks);

/// <summary>
/// Reassembles the raw byte stream that Media Foundation's fragmented MP4 sink
/// writes into complete top-level MP4 boxes, then groups them into the two units
/// Media Source Extensions accepts: one initialisation segment and a series of
/// moof+mdat media fragments.
///
/// Media Foundation does not promise that one <c>IMFByteStream.Write</c> call
/// equals one box, so the splitter is driven purely by the big-endian box
/// headers. It is deliberately free of COM and I/O so the acceptance tests can
/// drive it with synthetic bytes, including truncated and malformed input.
/// </summary>
internal sealed class Mp4ChunkAssembler
{
    /// <summary>A single top-level box larger than this is treated as a corrupt stream.</summary>
    public const int MaximumBoxBytes = 8 * 1024 * 1024;

    /// <summary>Bytes retained while waiting for the next moof or mdat before the stream is declared corrupt.</summary>
    public const int MaximumPendingBytes = 12 * 1024 * 1024;

    private const int BoxHeaderBytes = 8;
    private const int LargeBoxHeaderBytes = 16;

    private readonly List<byte> _pendingGroup = new(256 * 1024);
    private byte[] _buffer = new byte[128 * 1024];
    private int _start;
    private int _end;
    private int _sequence;
    private bool _initEmitted;
    private bool _groupOpen;

    public string? FailureReason { get; private set; }

    public bool HasFailed => FailureReason is not null;

    public bool InitEmitted => _initEmitted;

    /// <summary>Bytes buffered but not yet part of a complete chunk.</summary>
    public int PendingBytes => (_end - _start) + _pendingGroup.Count;

    /// <summary>
    /// Feeds bytes and returns every chunk that became complete. A failed
    /// assembler stays failed and returns nothing further, so a corrupt stream
    /// can never be mistaken for a short one.
    /// </summary>
    public IReadOnlyList<Mp4Chunk> Append(ReadOnlySpan<byte> data, long lastSubmittedFrameTicks, DateTimeOffset now)
    {
        if (HasFailed)
        {
            return [];
        }

        List<Mp4Chunk>? chunks = null;
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;

        while (true)
        {
            var available = _end - _start;
            if (available < BoxHeaderBytes)
            {
                break;
            }

            var boxLength = ReadUInt32(_buffer, _start);
            var headerBytes = BoxHeaderBytes;
            if (boxLength == 1)
            {
                if (available < LargeBoxHeaderBytes)
                {
                    break;
                }
                var large = ReadUInt64(_buffer, _start + BoxHeaderBytes);
                if (large > int.MaxValue)
                {
                    Fail("Media Foundation produced an MP4 box larger than this encoder supports.");
                    return chunks ?? [];
                }
                boxLength = (uint)large;
                headerBytes = LargeBoxHeaderBytes;
            }
            else if (boxLength == 0)
            {
                Fail("Media Foundation produced an MP4 box with an unbounded length.");
                return chunks ?? [];
            }

            if (boxLength < headerBytes)
            {
                Fail("Media Foundation produced an MP4 box with an invalid length.");
                return chunks ?? [];
            }
            if (boxLength > MaximumBoxBytes)
            {
                Fail($"Media Foundation produced an MP4 box of {boxLength} bytes, above the {MaximumBoxBytes}-byte streaming limit.");
                return chunks ?? [];
            }
            if (available < boxLength)
            {
                break;
            }

            var box = new ReadOnlySpan<byte>(_buffer, _start, (int)boxLength);
            var type = ReadType(_buffer, _start + 4);
            if (!Accept(type, box, lastSubmittedFrameTicks, now, ref chunks))
            {
                return chunks ?? [];
            }
            _start += (int)boxLength;
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }
        }

        return chunks ?? (IReadOnlyList<Mp4Chunk>)[];
    }

    private bool Accept(
        string type,
        ReadOnlySpan<byte> box,
        long lastSubmittedFrameTicks,
        DateTimeOffset now,
        ref List<Mp4Chunk>? chunks)
    {
        if (!_initEmitted)
        {
            // Everything up to and including moov belongs to the initialisation
            // segment. Emitting it as one unit means the page never has to guess
            // whether a fragment can be appended yet.
            _pendingGroup.AddRange(box);
            if (_pendingGroup.Count > MaximumPendingBytes)
            {
                Fail("The MP4 initialisation segment exceeded the streaming limit.");
                return false;
            }
            if (type == "moov")
            {
                _initEmitted = true;
                Emit(Mp4ChunkKind.Init, lastSubmittedFrameTicks, now, ref chunks);
            }
            return true;
        }

        switch (type)
        {
            case "moof":
                if (_groupOpen)
                {
                    Fail("Media Foundation produced a moof box before the previous fragment's mdat.");
                    return false;
                }
                _groupOpen = true;
                _pendingGroup.AddRange(box);
                break;
            case "mdat":
                _pendingGroup.AddRange(box);
                Emit(Mp4ChunkKind.Media, lastSubmittedFrameTicks, now, ref chunks);
                break;
            default:
                // sidx, free, styp and anything else are carried with the next
                // fragment rather than discarded, so the appended byte stream
                // stays a faithful prefix of what Media Foundation produced.
                _pendingGroup.AddRange(box);
                break;
        }

        if (_pendingGroup.Count > MaximumPendingBytes)
        {
            Fail("The pending MP4 fragment exceeded the streaming limit.");
            return false;
        }
        return true;
    }

    private void Emit(Mp4ChunkKind kind, long lastSubmittedFrameTicks, DateTimeOffset now, ref List<Mp4Chunk>? chunks)
    {
        if (_pendingGroup.Count == 0)
        {
            return;
        }
        chunks ??= [];
        chunks.Add(new Mp4Chunk(kind, _pendingGroup.ToArray(), _sequence++, now, lastSubmittedFrameTicks));
        _pendingGroup.Clear();
        if (kind == Mp4ChunkKind.Media)
        {
            _groupOpen = false;
        }
    }

    private void Fail(string reason)
    {
        FailureReason ??= reason;
    }

    private void EnsureCapacity(int additional)
    {
        var required = (_end - _start) + additional;
        if (required <= _buffer.Length - _start)
        {
            return;
        }
        // Compact before growing so a long stream cannot grow the buffer without bound.
        var live = _end - _start;
        if (live > 0 && _start > 0)
        {
            Array.Copy(_buffer, _start, _buffer, 0, live);
        }
        _start = 0;
        _end = live;
        if (live + additional <= _buffer.Length)
        {
            return;
        }
        var size = _buffer.Length;
        while (size < live + additional)
        {
            size = checked(size * 2);
        }
        Array.Resize(ref _buffer, size);
    }

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);

    private static ulong ReadUInt64(byte[] buffer, int offset) =>
        ((ulong)ReadUInt32(buffer, offset) << 32) | ReadUInt32(buffer, offset + 4);

    private static string ReadType(byte[] buffer, int offset) => string.Create(4, (Buffer: buffer, Offset: offset),
        static (span, state) =>
        {
            for (var index = 0; index < 4; index++)
            {
                var value = state.Buffer[state.Offset + index];
                span[index] = value is >= 0x20 and < 0x7F ? (char)value : '?';
            }
        });
}
