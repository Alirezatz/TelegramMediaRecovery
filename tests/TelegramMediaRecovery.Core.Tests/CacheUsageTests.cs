using System.Text.Json;
using TelegramMediaRecovery.Core.Browsing;
using TelegramMediaRecovery.Core.Media;
using TelegramMediaRecovery.Core.Reporting;

namespace TelegramMediaRecovery.Core.Tests;

public sealed class CacheUsageTests
{
    [Theory]
    [InlineData(0x904UL, (byte)1, false, CacheUsage.ProfilePhoto)]
    [InlineData(0x204UL, (byte)1, false, CacheUsage.DocumentThumbnail)]
    [InlineData(0x730504UL, (byte)1, false, CacheUsage.DocumentThumbnail)]
    [InlineData(0x030A04UL, (byte)2, false, CacheUsage.StickerSetThumbnail)]
    [InlineData(0x300UL, (byte)1, false, CacheUsage.AudioAlbumCover)]
    [InlineData(0x780804UL, (byte)1, false, CacheUsage.Photo)]
    [InlineData(0x104UL, (byte)0, false, CacheUsage.Document)]
    [InlineData(0x104UL, (byte)2, false, CacheUsage.Sticker)]
    [InlineData(0x104UL, (byte)5, false, CacheUsage.Document)]
    [InlineData(0x12340304UL, (byte)1, false, CacheUsage.Unknown)]
    [InlineData(10UL, (byte)1, false, CacheUsage.Unknown)]
    [InlineData(0x020400000904UL, (byte)1, false, CacheUsage.Unknown)]
    [InlineData(0x030000730504UL, (byte)1, false, CacheUsage.Unknown)]
    [InlineData(0x10000UL, (byte)0, true, CacheUsage.StreamedMedia)]
    [InlineData(0x20000UL, (byte)1, true, CacheUsage.StreamedMedia)]
    [InlineData(0x10000UL, (byte)2, true, CacheUsage.Sticker)]
    [InlineData(0x01080401UL, (byte)2, true, CacheUsage.StickerSetThumbnail)]
    [InlineData(10UL, (byte)1, true, CacheUsage.Unknown)]
    public void ScanUsesEncodedRolesAndKeepsSmallSquareImages(ulong high, byte tag, bool isStream, CacheUsage expected)
    {
        using var fixture = new RecoveryFixture();
        var bytes = RecoveryFixture.Bitmap(11); // Every case is the same tiny 2x2 image.
        fixture.Add(isStream ? "media_cache" : "cache", new CacheKey(high, isStream ? 0x10000UL : 1UL), bytes, tag);
        fixture.Commit();
        using var session = new RecoverySession(fixture.Source);
        var item = Assert.Single(session.Scan().Items);
        Assert.Equal(expected, item.Usage);
        Assert.Equal(bytes, session.ReadPreviewBytes(item));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public void SelectedExportPreservesRolesInJsonAndCsv()
    {
        using var fixture = new RecoveryFixture();
        fixture.Add("cache", new CacheKey(0x904, 1), RecoveryFixture.Bitmap(11));
        fixture.Add("cache", new CacheKey(0x780804, 2), RecoveryFixture.Bitmap(22));
        fixture.Commit();
        using var session = new RecoverySession(fixture.Source);
        var items = session.Scan().Items;
        var result = session.Export(items.Where(i => i.Usage == CacheUsage.Photo), fixture.Output);
        var written = Assert.Single(result.Report.Items);
        Assert.Equal(CacheUsage.Photo, written.CacheUsage);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Report.OutputDirectory, "manifest.json")));
        Assert.Equal("Photo", json.RootElement.GetProperty("Files")[0].GetProperty("CacheUsage").GetString());
        var lines = File.ReadAllLines(Path.Combine(result.Report.OutputDirectory, "manifest.csv"));
        var header = JsonManifestWriter.SplitCsvLine(lines[0]);
        var row = JsonManifestWriter.SplitCsvLine(lines[1]);
        Assert.Equal(header.Count, row.Count);
        Assert.Equal("Photo", row[header.IndexOf("cache_usage")]);
        Assert.Equal(RecoveryFixture.Bitmap(22), File.ReadAllBytes(written.OutputPath!));
    }
}
