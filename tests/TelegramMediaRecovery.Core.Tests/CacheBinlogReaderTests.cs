namespace TelegramMediaRecovery.Core.Tests;

/// <summary>
/// Tests for the binlog record framing, which is the single most important thing
/// this project has to get right.
/// </summary>
public sealed class CacheBinlogReaderTests
{
    private static PlaceId Place(int seed) => new([(byte)seed, 0xDE, 0xDB, 0x9A, 0x6F, 0xF5, 0x19]);

    [Fact]
    public void HeaderFlagsSelectTheTimestampedRecordSize()
    {
        var header = CacheBasicHeader.Parse(BinlogBuilder.Header(trackEstimatedTime: true));

        Assert.Equal(0, header.Format);
        Assert.True(header.TrackEstimatedTime);
        Assert.Equal(0x000001u, header.Flags);
        Assert.Equal(1_700_000_000u, header.SystemTime);
        Assert.False(CacheBasicHeader.Parse(BinlogBuilder.Header(trackEstimatedTime: false)).TrackEstimatedTime);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1481)]
    [InlineData(4096)]
    [InlineData(0x0F_FFFF)]      // 16 MiB - 1, the classic client maximum
    [InlineData(0x7F_FFFE)]      // just under the 128 MiB hard limit
    public void EntrySizeSpansAllThreeBytesOfTheTwentyFourByteField(int size)
    {
        var key = new CacheKey(0x1234, 0x5678);
        var binlog = Concat(
            BinlogBuilder.Header(trackEstimatedTime: false),
            BinlogBuilder.Store(key, 1, size, 0xDEAD_BEEF, Place(0x21)));

        var result = CacheBinlogReader.Parse(binlog, version: 0);

        var entry = Assert.Single(result.Entries).Value;
        Assert.Equal(size, entry.Size);
        Assert.Equal(0xDEAD_BEEFu, entry.Checksum);
        Assert.Equal(1, entry.Tag);
    }

    [Fact]
    public void StoreWithTimeRecordsAreEightyBytes()
    {
        var key = new CacheKey(0x15201, 0x440200001b6100f8);
        var binlog = Concat(
            BinlogBuilder.Header(trackEstimatedTime: true),
            BinlogBuilder.StoreWithTime(key, 1, 1481, 0x983578A5, Place(0x12), 0x6AAA9B23, 0x6AAA9B23));

        var result = CacheBinlogReader.Parse(binlog, version: 0);

        var entry = Assert.Single(result.Entries).Value;
        Assert.Equal(1481, entry.Size);
        Assert.Equal(key, entry.Key);
        Assert.Equal("21/EDBDA9F65F91", entry.Place.ToRelativePath());
        Assert.False(result.Truncated);
    }

    [Fact]
    public void MultiRemoveErasesKeys()
    {
        var keep = new CacheKey(1, 10);
        var drop = new CacheKey(2, 20);

        var binlog = Concat(
            BinlogBuilder.Header(trackEstimatedTime: false),
            BinlogBuilder.Store(keep, 1, 100, 1, Place(1)),
            BinlogBuilder.Store(drop, 1, 200, 2, Place(2)),
            BinlogBuilder.MultiRemove(drop));

        var result = CacheBinlogReader.Parse(binlog, version: 0);

        Assert.Single(result.Entries);
        Assert.True(result.Entries.ContainsKey(keep));
        Assert.False(result.Entries.ContainsKey(drop));
    }

    [Fact]
    public void MultiAccessOnlyRefreshesTheClock()
    {
        var key = new CacheKey(7, 7);
        var binlog = Concat(
            BinlogBuilder.Header(trackEstimatedTime: true),
            BinlogBuilder.StoreWithTime(key, 1, 100, 1, Place(1), 1_700_000_000, 1_000),
            BinlogBuilder.MultiAccess(relative: 1_500, systemTime: 1_700_000_500, key));

        var result = CacheBinlogReader.Parse(binlog, version: 0);

        var entry = Assert.Single(result.Entries).Value;
        Assert.Equal(100, entry.Size);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            entry.StoreTime.ToUniversalTime());

        // The entry's own clock reading is what survives: the access record only
        // refreshes the monotonic counter, never the wall clock stored with it.
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            entry.EstimatedLastUse.ToUniversalTime());
    }

    [Fact]
    public void MultiStorePartsAreAllApplied()
    {
        var first = new CacheKey(1, 1);
        var second = new CacheKey(1, 2);

        var binlog = Concat(
            BinlogBuilder.Header(trackEstimatedTime: false),
            BinlogBuilder.MultiStore(
                withTime: false,
                BinlogBuilder.Store(first, 1, 10, 0xAA, Place(1)),
                BinlogBuilder.Store(second, 2, 20, 0xBB, Place(2))));

        var result = CacheBinlogReader.Parse(binlog, version: 0);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(10, result.Entries[first].Size);
        Assert.Equal(2, result.Entries[second].Tag);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void AStoreAfterARemoveBringsTheKeyBack()
    {
        var key = new CacheKey(9, 9);
        var binlog = Concat(
            BinlogBuilder.Header(trackEstimatedTime: false),
            BinlogBuilder.Store(key, 1, 100, 1, Place(1)),
            BinlogBuilder.MultiRemove(key),
            BinlogBuilder.Store(key, 1, 300, 3, Place(2)));

        var result = CacheBinlogReader.Parse(binlog, version: 0);

        Assert.Equal(300, Assert.Single(result.Entries).Value.Size);
    }

    [Fact]
    public void AStoreWithSizeZeroIsRejected()
    {
        var record = BinlogBuilder.Store(new CacheKey(1, 1), 1, 100, 1, Place(1));
        record[8] = 0x00;
        record[16] = 0x00;
        record[24] = 0x00;

        var result = CacheBinlogReader.Parse(
            Concat(BinlogBuilder.Header(trackEstimatedTime: false), record),
            version: 0);

        Assert.Empty(result.Entries);
        Assert.Equal(1, result.RejectedRecords);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData(0x05)]
    [InlineData(0x09)]
    public void AnUnknownRecordTypeStopsTheScanWithoutLosingEarlierEntries(byte type)
    {
        var binlog = Concat(
            BinlogBuilder.Header(trackEstimatedTime: false),
            BinlogBuilder.Store(new CacheKey(1, 1), 1, 100, 1, Place(1)),
            [type, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        var result = CacheBinlogReader.Parse(binlog, version: 0);

        Assert.Single(result.Entries);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ATruncatedTailIsIgnoredButEarlierRecordsSurvive()
    {
        var key = new CacheKey(1, 1);
        var full = Concat(
            BinlogBuilder.Header(trackEstimatedTime: false),
            BinlogBuilder.Store(key, 1, 100, 1, Place(1)),
            BinlogBuilder.Store(key, 2, 200, 2, Place(2)));

        // Cut the last record in half: the parser must not invent an entry from it.
        var result = CacheBinlogReader.Parse(full[..(full.Length - 20)], version: 0);

        Assert.Single(result.Entries);
        Assert.Equal(100, Assert.Single(result.Entries).Value.Size);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void AnUnsupportedFormatIsRejected()
    {
        var header = BinlogBuilder.Header(trackEstimatedTime: false);
        header[0] = 9;

        Assert.Throws<InvalidDataException>(() => CacheBinlogReader.Parse(header, version: 0));
    }

    [Fact]
    public void APayloadShorterThanTheHeaderIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => CacheBinlogReader.Parse(new byte[8], version: 0));
    }

    /// <summary>
    /// Each byte of a place id is written low nibble first, so a rendered path is
    /// the nibble-swapped form of the raw bytes.
    /// </summary>
    [Theory]
    [InlineData("21/EDBDA9F65F91", "12DEDB9A6FF519")]
    [InlineData("00/000000000000", "00000000000000")]
    [InlineData("FF/FFFFFFFFFFFF", "FFFFFFFFFFFFFF")]
    [InlineData("1A/4F97510D3D99", "A1F47915D0D399")]
    public void PlaceIdRendersTheBucketFolderAndFileName(string expected, string rawHex)
    {
        var place = PlaceId.FromHex(rawHex);

        Assert.Equal(expected, place.ToRelativePath());
        Assert.Equal(rawHex, place.ToHex());
        Assert.Equal(place, PlaceId.FromHex(place.ToHex()));
    }

    [Fact]
    public void AccountFolderNamesAreRecognised()
    {
        Assert.True(CacheDatabaseLocator.IsAccountFolder("user_data"));
        Assert.True(CacheDatabaseLocator.IsAccountFolder("user_data#2"));
        Assert.True(CacheDatabaseLocator.IsAccountFolder("user_data#12"));
        Assert.False(CacheDatabaseLocator.IsAccountFolder("user_data#"));
        Assert.False(CacheDatabaseLocator.IsAccountFolder("user_dataX"));
        Assert.False(CacheDatabaseLocator.IsAccountFolder("temp"));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(static p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
