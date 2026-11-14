using TelegramMediaRecovery.Core.Cache;

namespace TelegramMediaRecovery.Core.Media;

/// <summary>One verified cached value. Only offsets and source references are retained.</summary>
public sealed class SliceFragment
{
    private readonly Func<CacheEntry, CacheValueLease?> _open;
    internal SliceFragment(CacheEntry entry, int sliceNumber, IReadOnlyList<PartMap.Range> parts,
        bool isContiguous, Func<CacheEntry, CacheValueLease?> open)
    {
        Entry = entry;
        SliceNumber = sliceNumber;
        Parts = parts;
        IsContiguous = isContiguous;
        _open = open;
    }
    public CacheEntry Entry { get; }
    public int SliceNumber { get; }
    public IReadOnlyList<PartMap.Range> Parts { get; }
    public bool IsContiguous { get; }
    public bool IsHeader => SliceNumber == 0;
    internal CacheValueLease Open() => _open(Entry)
        ?? throw new InvalidDataException("A cached fragment changed. Scan the data source again.");

    /// <summary>Compatibility accessor that explicitly materializes this value. Use Parts for metadata.</summary>
    public PartMap Map
    {
        get
        {
            using var value = Open();
            var bytes = value.Memory.ToArray();
            return PartMap.Create(Parts.Select(p => new PartMap.Part(p.Offset, p.Length, bytes.AsMemory(p.ValueOffset, p.Length))), bytes.Length);
        }
    }
}

/// <summary>A non-overlapping output range pointing into one source value.</summary>
public readonly record struct StreamedPart(SliceFragment Fragment, long Offset, int Length, int ValueOffset,
    long OriginOffset, int OriginLength)
{
    public long End => Offset + Length;
    internal StreamedPart Slice(long start, long end) => this with
    {
        Offset = start,
        Length = checked((int)(end - start)),
        ValueOffset = ValueOffset + checked((int)(start - Offset)),
    };
}

/// <summary>A small metadata plan. Media bytes are read only while scanning or writing a fragment.</summary>
public sealed class StreamedFilePlan
{
    internal StreamedFilePlan(CacheKey baseKey, IReadOnlyList<SliceFragment> fragments, IReadOnlyList<StreamedPart> ranges,
        long fileSize, bool hasHeader, bool looksLikeHeaderMap, byte[] head)
    {
        BaseKey = baseKey;
        Fragments = fragments;
        Ranges = ranges;
        FileSize = fileSize;
        CachedBytes = ranges.Sum(p => (long)p.Length);
        HasHeader = hasHeader;
        LooksLikeHeaderMap = looksLikeHeaderMap;
        Head = head;
    }
    public CacheKey BaseKey { get; }
    public IReadOnlyList<SliceFragment> Fragments { get; }
    public IReadOnlyList<StreamedPart> Ranges { get; }
    public ReadOnlyMemory<byte> Head { get; }
    public long FileSize { get; }
    public long CachedBytes { get; }
    public bool HasHeader { get; }
    public bool LooksLikeHeaderMap { get; }
    public double Coverage => FileSize <= 0 ? 0 : Math.Min(1, (double)CachedBytes / FileSize);
    public bool HasGaps => CachedBytes < FileSize;
    public StreamedFilePlan WithMinimumSize(long minimumSize) => minimumSize <= FileSize ? this
        : new StreamedFilePlan(BaseKey, Fragments, Ranges, minimumSize, HasHeader, LooksLikeHeaderMap, Head.ToArray());

    /// <summary>Explicitly materializes all parts for callers that need bytes. Recovery uses Ranges instead.</summary>
    public IReadOnlyList<PartMap.Part> AllParts()
    {
        var result = new List<PartMap.Part>(Ranges.Count);
        foreach (var group in Ranges.GroupBy(p => p.Fragment))
        {
            using var value = group.Key.Open();
            var bytes = value.Memory.ToArray();
            foreach (var part in group) { result.Add(new PartMap.Part(part.Offset, part.Length, bytes.AsMemory(part.ValueOffset, part.Length))); }
        }
        result.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return result;
    }
    public override string ToString() => $"{BaseKey}: {CachedBytes}/{FileSize} bytes ({Coverage:P1}) in {Fragments.Count} fragments";
}

/// <summary>Resolves overlap priorities while retaining only source ranges. Earlier offsets win; equal offsets keep the longer part.</summary>
internal sealed class StreamExtentMap
{
    private readonly List<StreamedPart> _ranges = [];
    public IReadOnlyList<StreamedPart> Ranges => _ranges;
    public IReadOnlyList<StreamedPart> Add(StreamedPart part)
    {
        var first = 0;
        var upper = _ranges.Count;
        while (first < upper)
        {
            var middle = (first + upper) / 2;
            if (_ranges[middle].End <= part.Offset) { first = middle + 1; } else { upper = middle; }
        }
        var last = first;
        var replacements = new List<StreamedPart>();
        var writes = new List<StreamedPart>();
        var cursor = part.Offset;
        void AddNew(long start, long end)
        {
            if (end <= start) { return; }
            var range = part.Slice(start, end);
            Append(replacements, range);
            Append(writes, range);
        }
        while (last < _ranges.Count && _ranges[last].Offset < part.End)
        {
            var previous = _ranges[last++];
            if (previous.Offset < part.Offset) { replacements.Add(previous.Slice(previous.Offset, part.Offset)); }
            var start = Math.Max(part.Offset, previous.Offset);
            var end = Math.Min(part.End, previous.End);
            AddNew(cursor, start);
            if (previous.OriginOffset < part.OriginOffset
                || (previous.OriginOffset == part.OriginOffset && previous.OriginLength >= part.OriginLength))
            {
                replacements.Add(previous.Slice(start, end));
            }
            else { AddNew(start, end); }
            cursor = end;
            if (previous.End > part.End) { replacements.Add(previous.Slice(part.End, previous.End)); }
        }
        AddNew(cursor, part.End);
        _ranges.RemoveRange(first, last - first);
        _ranges.InsertRange(first, replacements);
        return writes;
    }

    private static void Append(List<StreamedPart> parts, StreamedPart part)
    {
        if (parts.Count > 0 && parts[^1] is var previous && previous.Fragment == part.Fragment
            && previous.End == part.Offset && previous.ValueOffset + previous.Length == part.ValueOffset
            && previous.OriginOffset == part.OriginOffset && previous.OriginLength == part.OriginLength)
        {
            parts[^1] = previous with { Length = previous.Length + part.Length };
        }
        else { parts.Add(part); }
    }
}

/// <summary>Groups full file identities and inspects one source value at a time.</summary>
public static class StreamedFilePlanner
{
    public static IReadOnlyList<IGrouping<CacheKey, CacheEntry>> GroupByFile(IEnumerable<CacheEntry> entries) => entries
        .Where(e => e.Key.IsValid).GroupBy(e => BaseKey(e.Key)).ToList();

    public static StreamedFilePlan BuildPlan(IEnumerable<CacheEntry> group, Func<CacheEntry, ReadOnlyMemory<byte>?> readValue) =>
        BuildVerifiedPlan(group, entry => readValue(entry) is { } memory ? new CacheValueLease(memory) : null);

    internal static StreamedFilePlan BuildVerifiedPlan(IEnumerable<CacheEntry> group, Func<CacheEntry, CacheValueLease?> open,
        Action<StreamedPart, ReadOnlyMemory<byte>>? consume = null, CancellationToken cancellationToken = default)
    {
        var ordered = group.OrderBy(e => e.Key.Low).ToArray();
        if (ordered.Length == 0) { throw new ArgumentException("A streamed file needs at least one entry.", nameof(group)); }
        var baseKey = BaseKey(ordered[0].Key);
        var fragments = new List<SliceFragment>();
        var extents = new StreamExtentMap();
        var looksLikeHeaderMap = false;
        var head = Array.Empty<byte>();
        foreach (var entry in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delta = entry.Key.Low - baseKey.Low;
            if (entry.Key.High != baseKey.High || delta > StreamingLayout.MaxSlices) { continue; }
            using var value = open(entry);
            if (value is null || value.Memory.IsEmpty) { continue; }
            var sliceNumber = (int)delta;
            var parts = ReadRanges(value.Memory.Span, sliceNumber, out var contiguous, out var headerMap);
            looksLikeHeaderMap |= headerMap;
            var fragment = new SliceFragment(entry, sliceNumber, parts, contiguous, open);
            fragments.Add(fragment);
            foreach (var part in parts)
            {
                var candidate = new StreamedPart(fragment, part.Offset, part.Length, part.ValueOffset, part.Offset, part.Length);
                foreach (var write in extents.Add(candidate))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = value.Memory.Slice(write.ValueOffset, write.Length);
                    if (write.Offset == 0) { head = bytes.Span[..Math.Min(bytes.Length, 4096)].ToArray(); }
                    consume?.Invoke(write, bytes);
                }
            }
        }
        var ranges = extents.Ranges.ToArray();
        return new StreamedFilePlan(baseKey, fragments, ranges, ranges.Length == 0 ? 0 : ranges[^1].End,
            fragments.Any(f => f.IsHeader), looksLikeHeaderMap, head);
    }

    private static CacheKey BaseKey(CacheKey key) => new(key.High, key.Low & ~0x1FFUL);

    private static List<PartMap.Range> ReadRanges(ReadOnlySpan<byte> value, int sliceNumber, out bool contiguous, out bool headerMap)
    {
        headerMap = false;
        var offset = (long)Math.Max(0, sliceNumber - 1) * StreamingLayout.InSlice;
        var raw = MediaSniffer.LooksLikeMedia(value[..Math.Min(512, value.Length)]);
        if (sliceNumber == 0 && !raw && PartMap.TryReadRanges(value, 1L << 40, out var header, out var consumed))
        {
            headerMap = true;
            contiguous = false;
            if (consumed < value.Length)
            {
                if (PartMap.TryReadRanges(value[consumed..], StreamingLayout.InSlice, out var included, out _))
                {
                    header.AddRange(included.Select(p => p with { ValueOffset = p.ValueOffset + consumed }));
                }
                else { header.AddRange(RawRanges(value.Length - consumed, 0, consumed)); }
            }
            return header;
        }
        if (sliceNumber != 0 && !raw && value.Length % StreamingLayout.PartSize != 0
            && PartMap.TryReadRanges(value, StreamingLayout.InSlice, out var map, out _))
        {
            contiguous = false;
            return map.Select(p => p with { Offset = p.Offset + offset }).ToList();
        }
        contiguous = true;
        return RawRanges(value.Length, offset, 0);
    }

    private static List<PartMap.Range> RawRanges(int length, long fileOffset, int valueOffset)
    {
        var ranges = new List<PartMap.Range>();
        for (var index = 0; index < length; index += StreamingLayout.PartSize)
        {
            ranges.Add(new PartMap.Range(fileOffset + index, Math.Min(StreamingLayout.PartSize, length - index), valueOffset + index));
        }
        return ranges;
    }
}
