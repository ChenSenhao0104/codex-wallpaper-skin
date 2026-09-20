namespace CodexWallpaperSkin;

/// <summary>One Media Source Extensions unit with the immutable sequence number the page validates.</summary>
internal sealed record GpuFragment(int Sequence, byte[] Bytes);

/// <summary>
/// A bounded group of consecutive fragments delivered by one transport message.
///
/// The transport is batched on purpose. Media Foundation already emits one
/// fragment per frame, so sending every fragment separately would reproduce the
/// per-frame control traffic that Issue #2 asks the v0.4 path to remove; batching
/// a few frames per message cuts the message rate by an order of magnitude while
/// keeping every fragment individually appendable and in order.
/// </summary>
internal sealed record GpuFrameBatch(
    int FirstSequence,
    IReadOnlyList<GpuFragment> Fragments,
    DateTimeOffset CreatedAt,
    long LastFrameTicks)
{
    public int Count => Fragments.Count;

    public int ByteCount
    {
        get
        {
            var total = 0;
            foreach (var fragment in Fragments)
            {
                total += fragment.Bytes.Length;
            }
            return total;
        }
    }

    public string Describe() =>
        $"batch first={FirstSequence} count={Count} bytes={ByteCount}";
}

/// <summary>
/// Accumulates encoder fragments into bounded transport messages. Pure logic, so
/// the batching policy is unit tested without a GPU, a browser or a wallpaper.
/// </summary>
internal sealed class GpuStreamBatcher
{
    private readonly int _maximumFragments;
    private readonly int _maximumBytes;
    private readonly TimeSpan _maximumDelay;
    private readonly List<GpuFragment> _fragments = [];
    private int _bytes;
    private DateTimeOffset _oldest = DateTimeOffset.MaxValue;
    private long _lastFrameTicks;
    private bool _holdsInitSegment;

    public GpuStreamBatcher(int maximumFragments, int maximumBytes, TimeSpan maximumDelay)
    {
        _maximumFragments = Math.Max(1, maximumFragments);
        _maximumBytes = Math.Max(4096, maximumBytes);
        _maximumDelay = maximumDelay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(50) : maximumDelay;
    }

    public int Count => _fragments.Count;

    public int ByteCount => _bytes;

    public void Add(Mp4Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _fragments.Add(new GpuFragment(chunk.Sequence, chunk.Bytes));
        _bytes += chunk.Bytes.Length;
        if (_oldest == DateTimeOffset.MaxValue)
        {
            _oldest = chunk.CreatedAt;
        }
        _lastFrameTicks = chunk.LastSubmittedFrameTicks;
        if (chunk.Kind == Mp4ChunkKind.Init)
        {
            _holdsInitSegment = true;
        }
    }

    /// <summary>
    /// True when the pending group has reached a fragment, byte or age limit, or
    /// when it holds the initialisation segment. The init segment is never held
    /// back: the page cannot create its Media Source buffer until it arrives.
    /// </summary>
    public bool ShouldFlush(DateTimeOffset now) =>
        _fragments.Count > 0
        && (_holdsInitSegment
            || _fragments.Count >= _maximumFragments
            || _bytes >= _maximumBytes
            || now - _oldest >= _maximumDelay);

    /// <summary>Removes and returns the pending group, or null when nothing is pending.</summary>
    public GpuFrameBatch? Take(DateTimeOffset now)
    {
        if (_fragments.Count == 0)
        {
            return null;
        }
        var batch = new GpuFrameBatch(_fragments[0].Sequence, _fragments.ToArray(), now, _lastFrameTicks);
        _fragments.Clear();
        _bytes = 0;
        _oldest = DateTimeOffset.MaxValue;
        _holdsInitSegment = false;
        return batch;
    }
}
