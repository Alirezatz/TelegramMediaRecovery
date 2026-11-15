namespace TelegramMediaRecovery.Core.Tests;

public sealed class ExtractionBehaviorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OrphanScanRecoversUnreferencedPlaceFiles(bool computeHashes)
    {
        using var fixture = new RecoveryFixture();
        var key = new CacheKey(0x780804, 1);
        var bytes = RecoveryFixture.Bitmap(31);
        fixture.Add("cache", key, bytes);
        fixture.Commit();
        var orphan = MakeOrphan(fixture, key, 1);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(orphan)!, "0000000000FF"), "Not a TDEF file.");
        var before = fixture.SourceHashes();

        var report = Extract(fixture, computeHashes: computeHashes);

        var item = Assert.Single(report.Items);
        Assert.Equal("orphan", item.Source);
        Assert.True(File.ReadAllBytes(item.OutputPath!).AsSpan().StartsWith(bytes));
        Assert.Equal(1, Assert.Single(report.Databases).OrphanFilesRecovered);
        Assert.Equal(1, report.Counters.OrphanCandidates);
        Assert.Equal(before.OrderBy(p => p.Key), fixture.SourceHashes().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OrphanScanDoesNotRepeatIndexedFiles(bool computeHashes)
    {
        using var fixture = new RecoveryFixture();
        fixture.Add("cache", new CacheKey(0x780804, 1), RecoveryFixture.Bitmap(11));
        fixture.Add("cache", new CacheKey(0x780804, 2), RecoveryFixture.Bitmap(22));
        fixture.Commit();

        var report = Extract(fixture, computeHashes: computeHashes);

        Assert.Equal(2, report.Items.Count);
        Assert.All(report.Items, item => Assert.Equal("binlog", item.Source));
        Assert.Equal(0, Assert.Single(report.Databases).OrphanFilesRecovered);
        Assert.Equal(0, report.Counters.OrphanCandidates);
    }

    [Fact]
    public void FilteredIndexedFilesDoNotBecomeOrphans()
    {
        using var fixture = new RecoveryFixture();
        var small = new CacheKey(0x780804, 1);
        var large = new CacheKey(0x780804, 2);
        fixture.Add("cache", small, RecoveryFixture.Bitmap(11));
        fixture.Add("cache", large, RecoveryFixture.Bitmap(22, 86));
        fixture.Commit();

        var report = Extract(fixture, computeHashes: false, minimumSize: 80);

        var item = Assert.Single(report.Items);
        Assert.Equal(large, item.Key);
        Assert.Equal("binlog", item.Source);
        Assert.Contains(report.Skipped, item => item.Key == small && item.Reason == SkipReason.TooSmall);
        Assert.Equal(0, report.Counters.OrphanCandidates);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void StreamingLimitCountsFilesRatherThanSlices(int threads)
    {
        using var fixture = new RecoveryFixture();
        var originals = Enumerable.Range(1, 3).Select(n => RecoveryFixture.Wave((byte)n)).ToArray();
        for (var index = 0; index < originals.Length; index++)
        {
            AddStream(fixture, index + 1, originals[index]);
        }
        fixture.Commit();

        var report = Extract(fixture, limit: 1, threads: threads, computeHashes: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(2, item.FragmentCount);
        Assert.Contains(originals, bytes => File.ReadAllBytes(item.OutputPath!).SequenceEqual(bytes));
        Assert.Equal(2, report.Skipped.Count(item => item.Reason == SkipReason.LimitReached));
        Assert.Equal(0, report.Counters.OrphanCandidates);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void FileLimitIsAppliedSeparatelyToEachDatabase(int limit, int expectedPerDatabase)
    {
        using var fixture = new RecoveryFixture();
        for (var n = 1; n <= 3; n++)
        {
            fixture.Add("cache", new CacheKey(0x780804, (ulong)n), RecoveryFixture.Bitmap((byte)n));
            AddStream(fixture, n, RecoveryFixture.Wave((byte)n));
        }
        fixture.Commit();

        var report = Extract(fixture, limit: limit, computeHashes: false);

        Assert.Equal(2 * expectedPerDatabase, report.Items.Count);
        Assert.Equal(2, report.Databases.Count);
        Assert.All(report.Items.GroupBy(item => item.Database), items => Assert.Equal(expectedPerDatabase, items.Count()));
        Assert.All(report.Items, item => Assert.Equal("binlog", item.Source));
    }

    [Fact]
    public void OrphansShareTheDatabaseFileLimit()
    {
        using var fixture = new RecoveryFixture();
        for (var n = 1; n <= 3; n++)
        {
            fixture.Add("cache", new CacheKey(0x780804, (ulong)n), RecoveryFixture.Bitmap((byte)n));
        }
        fixture.Commit();
        MakeOrphan(fixture, new CacheKey(0x780804, 2), 2);
        MakeOrphan(fixture, new CacheKey(0x780804, 3), 3);

        var report = Extract(fixture, limit: 2, computeHashes: false);

        Assert.Equal(2, report.Items.Count);
        Assert.Single(report.Items, item => item.Source == "binlog");
        Assert.Single(report.Items, item => item.Source == "orphan");
        Assert.Equal(1, Assert.Single(report.Databases).OrphanFilesRecovered);
        Assert.Contains(report.Skipped, item => item.Reason == SkipReason.LimitReached);
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("media_cache")]
    public void OrphanOnlyExtractionHonorsTheFileLimit(string database)
    {
        using var fixture = new RecoveryFixture();
        for (var n = 1; n <= 3; n++)
        {
            fixture.Add(database, new CacheKey(0x10000, (ulong)n << 16), RecoveryFixture.Wave((byte)n));
        }
        fixture.Commit();
        for (var n = 1; n <= 3; n++)
        {
            MakeOrphan(fixture, new CacheKey(0x10000, (ulong)n << 16), n);
        }

        var report = Extract(fixture, limit: 1, computeHashes: false);

        Assert.Equal("orphan", Assert.Single(report.Items).Source);
        Assert.Equal(1, Assert.Single(report.Databases).OrphanFilesRecovered);
        Assert.Equal(2, report.Skipped.Count(item => item.Reason == SkipReason.LimitReached && item.Key == default));
    }

    [Fact]
    public void DisablingOrphanScanKeepsIndexedOnlyBehavior()
    {
        using var fixture = new RecoveryFixture();
        fixture.Add("cache", new CacheKey(0x780804, 1), RecoveryFixture.Bitmap(11));
        fixture.Add("cache", new CacheKey(0x780804, 2), RecoveryFixture.Bitmap(22));
        fixture.Commit();
        MakeOrphan(fixture, new CacheKey(0x780804, 2), 2);

        var report = Extract(fixture, scanOrphans: false);

        Assert.Equal("binlog", Assert.Single(report.Items).Source);
        Assert.Equal(0, report.Counters.OrphanCandidates);
    }

    private static ExtractionReport Extract(RecoveryFixture fixture, int limit = 0, int threads = 4,
        bool computeHashes = true, long minimumSize = 0, bool scanOrphans = true) =>
        new ExtractionEngine(new ExtractionOptions
        {
            OutputDirectory = fixture.Output,
            MaxFilesPerDatabase = limit,
            MaxDegreeOfParallelism = threads,
            ComputeHashes = computeHashes,
            MinimumSize = minimumSize,
            ScanOrphans = scanOrphans,
        }).Run(fixture.Source, LocalKeyLoader.Load(fixture.Source, string.Empty));

    private static void AddStream(RecoveryFixture fixture, int number, byte[] bytes)
    {
        var low = (ulong)number << 16;
        fixture.Add("media_cache", new CacheKey(0x10000, low), bytes[..128]);
        fixture.Add("media_cache", new CacheKey(0x10000, low + 1), bytes);
    }

    private static string MakeOrphan(RecoveryFixture fixture, CacheKey key, int number)
    {
        var source = fixture.Place(key);
        var version = Path.GetDirectoryName(Path.GetDirectoryName(source)!)!;
        var destination = Path.Combine(version, "FE", number.ToString("X12"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        return destination;
    }
}
