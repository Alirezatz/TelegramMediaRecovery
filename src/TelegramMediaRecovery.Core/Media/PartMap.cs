using System.Buffers.Binary;

namespace TelegramMediaRecovery.Core.Media;

/// <summary>
/// The "complex serialization" of a partially downloaded slice or of a file
/// header: a little list of already-fetched parts.
/// </summary>
/// <remarks>
/// Reference: <c>media_streaming_reader.cpp</c> &rarr;
/// <c>SerializeComplexPartsMap()</c> and <c>ParseComplexCachedMap()</c>:
/// <code>
/// uint32 count
/// repeat count times:
///     uint32 offset        // part-aligned, relative to the slice (header: to the file)
///     uint32 size
///     byte   data[size]
/// </code>
/// The count is bounded by <c>kMaxOnlyInHeader / kPartSize = 80</c> and every
/// <c>(offset, size)</c> pair must fit inside <c>maxSize</c>, which is how a
/// corrupt value is rejected instead of producing a gigabyte of garbage.
/// </remarks>
public sealed class PartMap
{
    /// <summary>A part of the reconstructed file.</summary>
    /// <param name="Offset">File-absolute offset of the part.</param>
    /// <param name="Length">Length in bytes.</param>
    /// <param name="Data">The bytes themselves.</param>
    public readonly record struct Part(int Offset, int Length, ReadOnlyMemory<byte> Data);

    private PartMap(List<Part> parts, int consumed)
    {
        Parts = parts;
        Consumed = consumed;
    }

    /// <summary>Builds a map from an explicit part list (used when merging maps).</summary>
    public static PartMap Create(IEnumerable<Part> parts, int consumed) => new([.. parts], consumed);

    /// <summary>
    /// Merges two maps, keeping the first part found at any given offset. This is
    /// exactly what the reader does with <c>PartsMap::try_emplace</c>, and it is
    /// what makes a header map and a folded-in first slice coexist.
    /// </summary>
    public static PartMap Combine(PartMap first, PartMap? second)
    {
        if (second is null)
        {
            return first;
        }

        var merged = first.Parts
            .Concat(second.Parts)
            .GroupBy(static p => p.Offset)
            .Select(static g => g.First())
            .OrderBy(static p => p.Offset)
            .ToList();

        return Create(merged, first.Consumed + second.Consumed);
    }

    /// <summary>Appends raw bytes at <paramref name="fileOffset"/> and merges them in.</summary>
    public static PartMap CombineWithRaw(PartMap first, ReadOnlySpan<byte> raw, int fileOffset) =>
        Combine(first, FromContiguous(raw, fileOffset));

    /// <summary>The parts, sorted by offset and without overlaps.</summary>
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>Number of bytes of the source value this map covers.</summary>
    public int Consumed { get; }

    /// <summary>Sum of the part lengths.</summary>
    public long TotalBytes => Parts.Sum(p => (long)p.Length);

    /// <summary>Offsets of the parts, used by <see cref="StreamingLayout.ComputeIsGoodHeader"/>.</summary>
    public IEnumerable<int> Offsets => Parts.Select(p => p.Offset);

    /// <summary>An empty map.</summary>
    public static PartMap Empty { get; } = new([], 0);

    /// <summary>
    /// Tries to parse a complex map from <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The decrypted cached value.</param>
    /// <param name="maxSize">
    /// Upper bound for offsets and lengths. Pass the file size for a header and
    /// <see cref="StreamingLayout.InSlice"/> for an ordinary slice.
    /// </param>
    /// <param name="map">The parsed map when the method returns <c>true</c>.</param>
    /// <returns><c>false</c> when the value is not a well-formed map.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, long maxSize, out PartMap map)
    {
        map = Empty;

        if (data.Length < 4)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var limit = StreamingLayout.MaxOnlyInHeader / StreamingLayout.PartSize;
        if (count == 0 || count > limit)
        {
            return false;
        }

        var parts = new List<Part>((int)count);
        var offset = 4;

        for (var i = 0; i < count; i++)
        {
            if (offset + 8 > data.Length)
            {
                return false;
            }

            var partOffset = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            offset += 4;
            var partSize = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            offset += 4;

            if (partSize <= 0
                || partOffset < 0
                || partOffset >= maxSize
                || partSize > maxSize
                || partOffset + (long)partSize > maxSize
                || offset + partSize > data.Length)
            {
                return false;
            }

            parts.Add(new Part(partOffset, partSize, data.Slice(offset, partSize).ToArray()));
            offset += partSize;
        }

        parts.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        map = new PartMap(parts, offset);
        return true;
    }

    /// <summary>
    /// Splits a contiguously serialised value into part-aligned chunks, mirroring
    /// the loop in <c>ParseCachedMap()</c>.
    /// </summary>
    public static PartMap FromContiguous(ReadOnlySpan<byte> data, int fileOffset)
    {
        var parts = new List<Part>();
        for (var offset = 0; offset < data.Length; offset += StreamingLayout.PartSize)
        {
            var length = Math.Min(StreamingLayout.PartSize, data.Length - offset);
            parts.Add(new Part(
                fileOffset + offset,
                length,
                data.Slice(offset, length).ToArray()));
        }

        return new PartMap(parts, data.Length);
    }
}
