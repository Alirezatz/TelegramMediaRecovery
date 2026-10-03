namespace TelegramMediaRecovery.Core.Tests;

/// <summary>
/// Tests for the streaming (sliced) media cache reconstruction.
/// </summary>
public sealed class StreamingTests
{
    [Fact]
    public void ContiguousSerializationIsDetectedByPartAlignment()
    {
        Assert.True(StreamingLayout.IsContiguousSerialization(StreamingLayout.PartSize, long.MaxValue));
        Assert.True(StreamingLayout.IsContiguousSerialization(StreamingLayout.InSlice, long.MaxValue));
        Assert.False(StreamingLayout.IsContiguousSerialization(1234, long.MaxValue));

        // The second condition is "the value exactly fills its slice".
        Assert.True(StreamingLayout.IsContiguousSerialization(1000, 1000));
    }

    [Fact]
    public void MaxSliceSizeMatchesTheClient()
    {
        const long Total = (2 * StreamingLayout.InSlice) + 1234;

        Assert.Equal(Total, StreamingLayout.MaxSliceSize(0, Total));
        Assert.Equal(StreamingLayout.InSlice, StreamingLayout.MaxSliceSize(1, Total));
        Assert.Equal(StreamingLayout.InSlice, StreamingLayout.MaxSliceSize(2, Total));
        Assert.Equal(1234, StreamingLayout.MaxSliceSize(3, Total));
    }

    [Fact]
    public void ComplexMapRoundTrips()
    {
        var first = RandomBytes(100);
        var second = RandomBytes(50);
        var payload = SerializeMap([Part(0, first), Part(128 * 1024, second)]);

        Assert.True(PartMap.TryParse(payload, long.MaxValue, out var map));

        Assert.Equal(2, map.Parts.Count);
        Assert.Equal(0, map.Parts[0].Offset);
        Assert.Equal(100, map.Parts[0].Length);
        Assert.Equal(128 * 1024, map.Parts[1].Offset);
        Assert.Equal(first, map.Parts[0].Data.ToArray());
        Assert.Equal(second, map.Parts[1].Data.ToArray());
    }

    [Fact]
    public void ComplexMapIsRejectedWhenAPartRunsPastTheBound()
    {
        var payload = SerializeMap([Part(0, RandomBytes(10))]);
        Assert.False(PartMap.TryParse(payload, maxSize: 4, out _));
    }

    [Fact]
    public void ComplexMapIsRejectedForAnImplausibleCount()
    {
        var data = new byte[8];
        BitConverter.TryWriteBytes(data, 1000);      // far beyond the 80-part limit

        Assert.False(PartMap.TryParse(data, long.MaxValue, out _));
    }

    [Fact]
    public void ContiguousDataIsSplitIntoPartAlignedChunks()
    {
        var data = new byte[(2 * StreamingLayout.PartSize) + 100];
        Random.Shared.NextBytes(data);

        var map = PartMap.FromContiguous(data, fileOffset: 512);

        Assert.Equal(3, map.Parts.Count);
        Assert.Equal(512, map.Parts[0].Offset);
        Assert.Equal(StreamingLayout.PartSize, map.Parts[0].Length);
        Assert.Equal(512 + StreamingLayout.PartSize, map.Parts[1].Offset);
        Assert.Equal(100, map.Parts[2].Length);
    }

    [Fact]
    public void ASmallFileStoredAsOneRawHeaderValueIsReassembledWhole()
    {
        var jpeg = BuildJpeg(2048);
        var plan = StreamedFilePlanner.BuildPlan(Group([Entry(1_000, 0, jpeg)]), _ => jpeg);

        Assert.True(plan.HasHeader);
        Assert.False(plan.LooksLikeHeaderMap);
        Assert.Equal(jpeg.Length, plan.FileSize);
        Assert.Equal(1.0, plan.Coverage, 3);
        Assert.Equal(MediaKind.Image, MediaSniffer.Detect(plan.AllParts()[0].Data.Span[..512]).Kind);
    }

    [Fact]
    public void SlicesArePlacedAtTheirFileOffsets()
    {
        // The base value is neither recognisable media nor a valid part map, so it
        // is treated as raw bytes at offset 0; slice 1 lands at kInSlice.
        var baseValue = BuildJpeg(64);
        var slice = new byte[StreamingLayout.InSlice];

        var entries = new[] { Entry(0x42, 0, baseValue), Entry(0x42, 1, slice) };
        var plan = StreamedFilePlanner.BuildPlan(
            Group(entries),
            entry => entry.Key.Low == 0 ? baseValue : slice);

        Assert.Equal(StreamingLayout.InSlice, plan.FileSize);
        Assert.Equal(2, plan.Fragments.Count);
        Assert.Contains(plan.AllParts(), p => p.Offset == 0 && p.Length == 64);
        Assert.Contains(plan.AllParts(), p => p.Offset + p.Length == StreamingLayout.InSlice);
    }

    [Fact]
    public void AGoodHeaderCarriesTheFirstSliceAsASecondMap()
    {
        // Header map: one part at kInSlice + 4096 (the index at the end of the
        // file), followed by the folded-in first slice: parts at 0 and 128 KiB.
        var header = SerializeMap([Part(StreamingLayout.InSlice + 4096, RandomBytes(32))])
            .Concat(SerializeMap(
            [
                Part(0, RandomBytes(16)),
                Part(StreamingLayout.PartSize, RandomBytes(16)),
            ]))
            .ToArray();

        var plan = StreamedFilePlanner.BuildPlan(Group([Entry(1, 0, header)]), _ => header);

        Assert.True(plan.LooksLikeHeaderMap);
        Assert.Equal(
            [0, StreamingLayout.PartSize, StreamingLayout.InSlice + 4096],
            plan.AllParts().Select(static p => p.Offset).Order().ToArray());
    }

    [Fact]
    public void ACompleteSingleValueHasFullCoverage()
    {
        var jpeg = BuildJpeg(1024);
        var plan = StreamedFilePlanner.BuildPlan(Group([Entry(9, 0, jpeg)]), _ => jpeg);

        Assert.Equal(1.0, plan.Coverage, 3);
        Assert.False(plan.HasGaps);
    }

    [Fact]
    public void AMissingMiddleSliceLeavesAGap()
    {
        var header = BuildJpeg(1024);
        var slice = new byte[StreamingLayout.InSlice];
        var entries = new[] { Entry(5, 0, header), Entry(5, 2, slice) };

        var plan = StreamedFilePlanner.BuildPlan(
            Group(entries),
            entry => entry.Key.Low == 0 ? header : slice);

        Assert.True(plan.HasGaps);
        Assert.Equal(2 * StreamingLayout.InSlice, plan.FileSize);
        Assert.InRange(plan.Coverage, 0.5, 0.7);
    }

    // ----------------------------------------------------------------- helpers

    private static IGrouping<ulong, CacheEntry> Group(IEnumerable<CacheEntry> entries) =>
        entries.GroupBy(static e => e.Key.High).Single();

    private static CacheEntry Entry(ulong high, ulong low, byte[] value) => new(
        new CacheKey(high, low),
        new PlaceId([1, 2, 3, 4, 5, 6, 7]),
        value.Length,
        0,
        0,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        0);

    private static byte[] BuildJpeg(int length)
    {
        var data = new byte[length];
        data[0] = 0xFF;
        data[1] = 0xD8;
        data[2] = 0xFF;
        data[3] = 0xE0;
        Random.Shared.NextBytes(data.AsSpan(4));
        return data;
    }

    /// <summary>One serialised part: offset, size, then the bytes.</summary>
    private static byte[] Part(int offset, byte[] data)
    {
        var result = new List<byte>(8 + data.Length);
        result.AddRange(Int32(offset));
        result.AddRange(Int32(data.Length));
        result.AddRange(data);
        return [.. result];
    }

    private static byte[] SerializeMap(IReadOnlyList<byte[]> parts)
    {
        var result = new List<byte>();
        result.AddRange(Int32(parts.Count));
        foreach (var part in parts)
        {
            result.AddRange(part);
        }

        return [.. result];
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        return bytes;
    }

    private static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }
}
