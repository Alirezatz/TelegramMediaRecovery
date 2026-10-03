using TelegramMediaRecovery.Core.Cache;

namespace TelegramMediaRecovery.Core.Media;

/// <summary>
/// One cached value that contributes parts to a streamed file.
/// </summary>
public sealed class SliceFragment
{
    public SliceFragment(CacheEntry entry, int sliceNumber, PartMap map, bool isContiguous)
    {
        Entry = entry;
        SliceNumber = sliceNumber;
        Map = map;
        IsContiguous = isContiguous;
    }

    /// <summary>The binlog entry the bytes came from.</summary>
    public CacheEntry Entry { get; }

    /// <summary>0 for the file header, 1..N for the slices after it.</summary>
    public int SliceNumber { get; }

    /// <summary>The parts this value contributes.</summary>
    public PartMap Map { get; }

    /// <summary>True when the value was stored as plain bytes rather than a map.</summary>
    public bool IsContiguous { get; }

    /// <summary>True when this fragment is the header slice of its file.</summary>
    public bool IsHeader => SliceNumber == 0;
}

/// <summary>
/// The result of reassembling every cached fragment of one streamed file.
/// </summary>
public sealed class StreamedFilePlan
{
    public StreamedFilePlan(
        CacheKey baseKey,
        IReadOnlyList<SliceFragment> fragments,
        long fileSize,
        long cachedBytes,
        bool hasHeader,
        bool looksLikeHeaderMap)
    {
        BaseKey = baseKey;
        Fragments = fragments;
        FileSize = fileSize;
        CachedBytes = cachedBytes;
        HasHeader = hasHeader;
        LooksLikeHeaderMap = looksLikeHeaderMap;
    }

    /// <summary>Lowest key of the file (<c>CacheHelper::key(0)</c>).</summary>
    public CacheKey BaseKey { get; }

    /// <summary>Fragments in the order they were read.</summary>
    public IReadOnlyList<SliceFragment> Fragments { get; }

    /// <summary>Reconstructed size: the end of the last known part.</summary>
    public long FileSize { get; }

    /// <summary>Number of bytes actually present in the cache.</summary>
    public long CachedBytes { get; }

    /// <summary>True when slice 0 of the file is present.</summary>
    public bool HasHeader { get; }

    /// <summary>
    /// True when the first fragment is a header map rather than raw bytes, i.e.
    /// the file is larger than one slice and its metadata was stored separately
    /// from the body.
    /// </summary>
    public bool LooksLikeHeaderMap { get; }

    /// <summary>Fraction of the file that is really cached, 0..1.</summary>
    public double Coverage => FileSize <= 0 ? 0 : Math.Min(1.0, (double)CachedBytes / FileSize);

    /// <summary>True when at least one gap has to be filled with zeros.</summary>
    public bool HasGaps => CachedBytes < FileSize;

    /// <summary>Every part of the plan, sorted by file offset and non-overlapping.</summary>
    public IReadOnlyList<PartMap.Part> AllParts() =>
        Fragments
            .SelectMany(static f => f.Map.Parts)
            .GroupBy(static p => p.Offset)
            .Select(static g => g.First())
            .OrderBy(static p => p.Offset)
            .ToList();

    /// <inheritdoc />
    public override string ToString() =>
        $"{BaseKey}: {CachedBytes}/{FileSize} bytes ({Coverage:P1}) in {Fragments.Count} fragments";
}

/// <summary>
/// Groups the entries of a streaming database into files and works out how each
/// cached value contributes to them.
/// </summary>
/// <remarks>
/// The client writes a streamed file at keys <c>baseKey.low + sliceNumber</c>
/// (<c>media_streaming_reader.cpp</c> &rarr; <c>Reader::CacheHelper::key()</c>), so
/// entries sharing one <see cref="CacheKey.High"/> with consecutive
/// <see cref="CacheKey.Low"/> values belong to the same file. The base (slice 0)
/// is the lowest such value; every later value is slice <c>low - base.low - 1</c>.
/// <para>
/// Two serializations exist:
/// <list type="bullet">
///   <item><description>
///     <b>Contiguous</b> &mdash; the raw bytes of the slice, which the reader
///     splits into part-aligned chunks.
///   </description></item>
///   <item><description>
///     <b>Complex</b> &mdash; an explicit part list, used when only some parts
///     arrived. The header always uses this form, optionally followed by the first
///     slice's own map when the header is "good" (see
///     <see cref="StreamingLayout.ComputeIsGoodHeader"/>).
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public static class StreamedFilePlanner
{
    /// <summary>Groups entries by <see cref="CacheKey.High"/>.</summary>
    public static IReadOnlyList<IGrouping<ulong, CacheEntry>> GroupByFile(
        IEnumerable<CacheEntry> entries) =>
        entries
            .Where(static e => e.Key.IsValid)
            .GroupBy(static e => e.Key.High)
            .ToList();

    /// <summary>Builds the reassembly plan for one <c>High</c> group.</summary>
    /// <param name="group">Entries sharing one <see cref="CacheKey.High"/>.</param>
    /// <param name="readValue">
    /// Decrypts one entry. Returning <c>null</c> means the value has already been
    /// pruned from disk, which happens whenever the client drops a file after the
    /// binlog was written.
    /// </param>
    public static StreamedFilePlan BuildPlan(
        IGrouping<ulong, CacheEntry> group,
        Func<CacheEntry, ReadOnlyMemory<byte>?> readValue)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(readValue);

        var ordered = group.OrderBy(static e => e.Key.Low).ToList();
        var baseKey = ordered[0].Key;

        var fragments = new List<SliceFragment>(ordered.Count);
        var maps = new List<PartMap>();
        long fileSize = 0;
        long cachedBytes = 0;
        var hasHeader = false;
        var looksLikeHeaderMap = false;

        for (var index = 0; index < ordered.Count; index++)
        {
            var entry = ordered[index];
            var maybeValue = readValue(entry);
            if (maybeValue is not { } value || value.Length == 0)
            {
                continue;
            }

            var sliceNumber = (int)(entry.Key.Low - baseKey.Low);
            if (sliceNumber < 0 || sliceNumber > StreamingLayout.MaxSlices)
            {
                continue;
            }

            if (index == 0)
            {
                hasHeader = true;
            }

            var sliceOffset = (long)Math.Max(0, sliceNumber - 1) * StreamingLayout.InSlice;
            PartMap map;
            bool isContiguous;

            if (sliceNumber == 0)
            {
                map = BuildHeaderMap(value.Span, ref looksLikeHeaderMap);
                isContiguous = !looksLikeHeaderMap;
            }
            else if (IsLikelyContiguous(value.Span)
                     || !PartMap.TryParse(value.Span, StreamingLayout.InSlice, out var sliceMap))
            {
                map = PartMap.FromContiguous(value.Span, (int)sliceOffset);
                isContiguous = true;
            }
            else
            {
                map = sliceOffset == 0 ? sliceMap : Shift(sliceMap, sliceOffset);
                isContiguous = false;
            }

            fragments.Add(new SliceFragment(entry, sliceNumber, map, isContiguous));
            maps.Add(map);

            foreach (var part in map.Parts)
            {
                fileSize = Math.Max(fileSize, part.Offset + (long)part.Length);
                cachedBytes += part.Length;
            }
        }

        return new StreamedFilePlan(baseKey, fragments, fileSize, cachedBytes, hasHeader, looksLikeHeaderMap);
    }

    /// <summary>
    /// Interprets the base value of a file: either a header map (optionally
    /// followed by the folded-in first slice) or the raw bytes of a whole file
    /// that fits into the header slice.
    /// </summary>
    private static PartMap BuildHeaderMap(ReadOnlySpan<byte> data, ref bool looksLikeHeaderMap)
    {
        // A raw file always starts with a recognisable container signature while a
        // map always starts with a small part count, so checking the signature
        // first removes the ambiguity the client resolves using the true file size.
        if (LooksLikeRawMedia(data))
        {
            return PartMap.FromContiguous(data, 0);
        }

        if (!PartMap.TryParse(data, MaxHeaderOffset, out var header))
        {
            return PartMap.FromContiguous(data, 0);
        }

        looksLikeHeaderMap = true;

        // A "good header" carries the first slice's own map right after the header
        // map. That second map's offsets already start at 0, so it can be merged
        // in as is. Anything left over is treated as raw bytes at offset 0.
        if (header.Consumed >= data.Length)
        {
            return header;
        }

        var rest = data[header.Consumed..];
        return PartMap.TryParse(rest, StreamingLayout.InSlice, out var included)
            ? PartMap.Combine(header, included)
            : PartMap.CombineWithRaw(header, rest, 0);
    }

    private const long MaxHeaderOffset = 1L << 40;

    private static PartMap Shift(PartMap map, long delta) => PartMap.Create(
        map.Parts.Select(p => new PartMap.Part(p.Offset + (int)delta, p.Length, p.Data)),
        map.Consumed);

    /// <summary>
    /// True when the value must be plain bytes: either its length is part aligned
    /// (which is what the client guarantees for a contiguous serialization) or it
    /// starts with a media signature.
    /// </summary>
    private static bool IsLikelyContiguous(ReadOnlySpan<byte> data) =>
        data.Length % StreamingLayout.PartSize == 0 || LooksLikeRawMedia(data);

    private static bool LooksLikeRawMedia(ReadOnlySpan<byte> data) =>
        MediaSniffer.LooksLikeMedia(data[..Math.Min(data.Length, 512)]);
}
