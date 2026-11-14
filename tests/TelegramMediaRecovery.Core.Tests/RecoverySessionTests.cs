using TelegramMediaRecovery.Core.Browsing;

namespace TelegramMediaRecovery.Core.Tests;

public sealed class RecoverySessionTests
{
    [Fact]
    public void StandardLayoutIsDetectedAndAllRecordsAreFramed()
    {
        var first = BinlogBuilder.CompactStore(new CacheKey(10, 1), 1234, 22, new PlaceId([1, 2, 3, 4, 5, 6, 7]));
        var second = BinlogBuilder.CompactStore(new CacheKey(20, 1), 2345, 33, new PlaceId([8, 7, 6, 5, 4, 3, 2]));
        var parsed = CacheBinlogReader.Parse([.. BinlogBuilder.Header(true), .. first, .. second], 0);
        Assert.Equal(CacheRecordFormat.TelegramDesktop, parsed.RecordFormat);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.False(parsed.Truncated);
        Assert.Equal(1234, parsed.Entries[new CacheKey(10, 1)].Size);
    }

    [Fact]
    public void ScanFindsMediaWithoutWritingOrChangingSourceFiles()
    {
        using var fixture = Images();
        var before = fixture.SourceHashes();
        using var session = new RecoverySession(fixture.Source);
        var catalog = session.Scan();
        Assert.Equal(2, catalog.Items.Count);
        Assert.False(Directory.Exists(fixture.Output));
        Assert.Equal(before.OrderBy(p => p.Key), fixture.SourceHashes().OrderBy(p => p.Key));
        Assert.All(catalog.Items, item => Assert.False(item.IsStream));
    }

    [Fact]
    public void PreviewReadsOnlyTheRequestedIdentityAndDoesNotExportIt()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var catalog = session.Scan();
        File.Delete(fixture.Place(new CacheKey(20, 1)));
        Assert.Equal(RecoveryFixture.Bitmap(11), session.ReadPreviewBytes(catalog.Items.Single(i => i.Entries[0].Key.High == 10)));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public void ExportWritesOnlySelectedFilesAndCsvColumnsMatch()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var catalog = session.Scan();
        var result = session.Export([catalog.Items[0]], fixture.Output);
        Assert.Single(result.Report.Items);
        Assert.Empty(result.Failures);
        Assert.Equal(RecoveryFixture.Bitmap(11), File.ReadAllBytes(result.Report.Items[0].OutputPath!));
        var csv = File.ReadAllLines(Path.Combine(result.Report.OutputDirectory, "manifest.csv"));
        Assert.Equal(csv[0].Split(',').Length, csv[1].Split(',').Length);
    }

    [Fact]
    public void DistinctStreamsWithIdenticalLayoutsAreBothExported()
    {
        using var fixture = new RecoveryFixture();
        var first = RecoveryFixture.Wave(11);
        var second = RecoveryFixture.Wave(22);
        fixture.Add("media_cache", new CacheKey(10, 0x10000), first);
        fixture.Add("media_cache", new CacheKey(10, 0x20000), second);
        fixture.Commit();
        using var session = new RecoverySession(fixture.Source);
        var catalog = session.Scan();
        var result = session.Export(catalog.Items, fixture.Output);
        Assert.Equal(2, result.Report.Items.Count);
        Assert.Equal(2, result.Report.Items.Select(i => i.Sha256).Distinct().Count());
        Assert.Contains(result.Report.Items, i => File.ReadAllBytes(i.OutputPath!).SequenceEqual(first));
        Assert.Contains(result.Report.Items, i => File.ReadAllBytes(i.OutputPath!).SequenceEqual(second));
    }

    [Fact]
    public void RepeatedExportsUseSeparateFoldersAndPreserveEarlierBytes()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var items = session.Scan().Items;
        var first = session.Export([items[0]], fixture.Output);
        var second = session.Export([items[1]], fixture.Output);
        Assert.NotEqual(first.Report.OutputDirectory, second.Report.OutputDirectory);
        Assert.Equal(RecoveryFixture.Bitmap(11), File.ReadAllBytes(first.Report.Items[0].OutputPath!));
        Assert.Equal(RecoveryFixture.Bitmap(22, 86), File.ReadAllBytes(second.Report.Items[0].OutputPath!));
    }

    [Fact]
    public void ExportInsideTheSourceIsRejectedBeforeAnyFileIsWritten()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var catalog = session.Scan();
        var forbidden = Path.Combine(fixture.Source, "export");
        Assert.Throws<ArgumentException>(() => session.Export(catalog.Items, forbidden));
        Assert.False(Directory.Exists(forbidden));
        Assert.Throws<ArgumentException>(() => session.Export(catalog.Items, fixture.Source));
    }

    [Fact]
    public void CancellationBeforeExportCreatesNoOutput()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var catalog = session.Scan();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Export(catalog.Items, fixture.Output, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public void CancellationDuringExportKeepsAReportForCompletedFiles()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var catalog = session.Scan();
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(_ => cancellation.Cancel());
        var result = session.Export(catalog.Items, fixture.Output, progress, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Single(result.Report.Items);
        Assert.True(File.Exists(Path.Combine(result.Report.OutputDirectory, "manifest.json")));
        Assert.Empty(Directory.GetFiles(result.Report.OutputDirectory, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ChangedCacheEntriesAreReportedInsteadOfExportingWrongBytes()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var catalog = session.Scan();
        File.Delete(fixture.Place(new CacheKey(10, 1)));
        var result = session.Export([catalog.Items[0]], fixture.Output);
        Assert.Empty(result.Report.Items);
        Assert.Single(result.Failures);
        Assert.Empty(Directory.GetFiles(result.Report.OutputDirectory, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ANewScanInvalidatesOldSelections()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var old = session.Scan().Items;
        session.Scan();
        Assert.Throws<ArgumentException>(() => session.Export(old, fixture.Output));
    }

    [Fact]
    public void APreviewLeaseDeletesItsTemporaryFile()
    {
        using var fixture = Images();
        using var session = new RecoverySession(fixture.Source);
        var item = session.Scan().Items[0];
        var preview = session.CreatePreview(item);
        Assert.True(File.Exists(preview.Path));
        Assert.Equal(RecoveryFixture.Bitmap(11), File.ReadAllBytes(preview.Path));
        preview.Dispose();
        Assert.False(File.Exists(preview.Path));
    }

    [Fact]
    public void TheLocalPasscodeIsRequiredWhenTheSourceUsesOne()
    {
        using var fixture = new RecoveryFixture("test-passcode");
        fixture.Add("cache", new CacheKey(10, 1), RecoveryFixture.Bitmap(11));
        fixture.Commit();
        Assert.Throws<LocalBlobCipher.WrongPasscodeException>(() => new RecoverySession(fixture.Source));
        using var session = new RecoverySession(fixture.Source, "test-passcode");
        Assert.Single(session.Scan().Items);
    }

    private static RecoveryFixture Images()
    {
        var fixture = new RecoveryFixture();
        fixture.Add("cache", new CacheKey(10, 1), RecoveryFixture.Bitmap(11));
        fixture.Add("cache", new CacheKey(20, 1), RecoveryFixture.Bitmap(22, 86));
        fixture.Commit();
        return fixture;
    }

    [Fact]
    public void MissingMetadataHeaderDoesNotShiftOrDiscardSurvivingBodySlices()
    {
        var first = new byte[StreamingLayout.InSlice];
        var second = new byte[StreamingLayout.InSlice];
        RecoveryFixture.Wave(11).CopyTo(first, 0);
        second.AsSpan().Fill(22);
        var entries = new[]
        {
            new CacheEntry(new CacheKey(10, 0x10001), PlaceId.Empty, first.Length, 0, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0),
            new CacheEntry(new CacheKey(10, 0x10002), PlaceId.Empty, second.Length, 0, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0),
        };
        var plan = StreamedFilePlanner.BuildPlan(entries, e => e.Key.Low == 0x10001 ? first : second);
        Assert.False(plan.HasHeader);
        Assert.Equal(2L * StreamingLayout.InSlice, plan.FileSize);
        Assert.Equal(plan.FileSize, plan.CachedBytes);
        Assert.False(plan.HasGaps);
        Assert.Equal(2L * StreamingLayout.InSlice, plan.AllParts().Max(p => p.Offset + p.Length));
    }
    private sealed class ImmediateProgress(Action<RecoveryProgress> report) : IProgress<RecoveryProgress>
    {
        public void Report(RecoveryProgress value) => report(value);
    }

    [Fact]
    public void ScanProgressReachesCompletionOnlyAfterAllDatabases()
    {
        using var fixture = Images();
        fixture.Add("media_cache", new CacheKey(0x10000, 0x10000), RecoveryFixture.Wave(11));
        fixture.Commit();
        using var session = new RecoverySession(fixture.Source);
        var updates = new List<RecoveryProgress>();
        session.Scan(new ImmediateProgress(update => { lock (updates) { updates.Add(update); } }));
        Assert.True(updates.Count >= 2);
        Assert.All(updates.SkipLast(1), update => Assert.True(update.Completed < update.Total));
        Assert.Equal(updates[^1].Total, updates[^1].Completed);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ExportUsesCurrentStreamGapsWhenASliceWasEvictedAfterScanning(int lostSlice)
    {
        using var fixture = new RecoveryFixture();
        for (var n = 1; n <= 3; n++)
        {
            var slice = new byte[StreamingLayout.InSlice];
            slice.AsSpan().Fill((byte)n);
            if (n == 1) { RecoveryFixture.Wave(11).CopyTo(slice, 0); }
            fixture.Add("media_cache", new CacheKey(0x10000, 0x10000UL + (ulong)n), slice);
        }
        fixture.Commit();
        using var session = new RecoverySession(fixture.Source);
        var scanned = Assert.Single(session.Scan().Items);
        Assert.False(scanned.HasGaps);
        File.Delete(fixture.Place(new CacheKey(0x10000, 0x10000UL + (ulong)lostSlice)));
        var export = session.Export([scanned], fixture.Output);
        var written = Assert.Single(export.Report.Items);
        Assert.Empty(export.Failures);
        Assert.True(written.IsPartial);
        Assert.Equal(2d / 3d, written.Coverage, 5);
        Assert.Contains(".partial.", written.OutputPath!);
        Assert.Equal(2, written.FragmentCount);
    }
}
