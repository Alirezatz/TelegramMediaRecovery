namespace TelegramMediaRecovery.Core.Tests;

/// <summary>
/// Tests for container sniffing and the small format readers.
/// </summary>
public sealed class FormatTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 }, "jpg", MediaKind.Image)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png", MediaKind.Image)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "gif", MediaKind.Image)]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x00 }, "mkv", MediaKind.Video)]
    [InlineData(new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00 }, "flac", MediaKind.Audio)]
    [InlineData(new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00 }, "mp3", MediaKind.Audio)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "pdf", MediaKind.Document)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14 }, "zip", MediaKind.Document)]
    [InlineData(new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00 }, "ogg", MediaKind.Audio)]
    [InlineData(new byte[] { 0x4F, 0x54, 0x54, 0x4F, 0x00 }, "otf", MediaKind.Document)]
    public void KnownMagicsAreRecognised(byte[] head, string extension, MediaKind kind)
    {
        var format = MediaSniffer.Detect(head);

        Assert.Equal(extension, format.Extension);
        Assert.Equal(kind, format.Kind);
    }

    [Fact]
    public void WebpWavAndAviShareTheRiffSignature()
    {
        Assert.Equal("webp", MediaSniffer.Detect(BuildRiff("WEBP")).Extension);
        Assert.Equal("wav", MediaSniffer.Detect(BuildRiff("WAVE")).Extension);
        Assert.Equal("avi", MediaSniffer.Detect(BuildRiff("AVI ")).Extension);
    }

    [Theory]
    [InlineData("isom", "mp4")]
    [InlineData("M4A ", "m4a")]
    [InlineData("qt  ", "mov")]
    [InlineData("3gp4", "3gp")]
    [InlineData("avif", "avif")]
    [InlineData("heic", "heic")]
    public void IsoBaseMediaBrandsPickTheExtension(string brand, string extension)
    {
        Assert.Equal(extension, MediaSniffer.Detect(BuildIso(brand)).Extension);
    }

    [Fact]
    public void AGzippedLottieStickerBecomesTgs()
    {
        const string json = """{"v":"5.7.4","fr":30,"ip":0,"op":60,"w":512,"h":512,"layers":[]}""";
        var format = MediaSniffer.Detect(Gzip(json));

        Assert.Equal("tgs", format.Extension);
        Assert.Equal(MediaKind.Animation, format.Kind);
    }

    [Fact]
    public void ARealGzipArchiveStaysGz()
    {
        const string payload = "just some text, not lottie, definitely not a sticker payload";

        Assert.Equal("gz", MediaSniffer.Detect(Gzip(payload)).Extension);
    }

    [Fact]
    public void UnknownBytesAreReportedAsBinary()
    {
        var head = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22, 0x33 };

        Assert.Equal(MediaFormat.Binary, MediaSniffer.Detect(head));
        Assert.False(MediaSniffer.LooksLikeMedia(head));
    }

    [Fact]
    public void VeryShortInputIsNeverMisidentified()
    {
        Assert.Equal(MediaFormat.Binary, MediaSniffer.Detect([]));
        Assert.Equal(MediaFormat.Binary, MediaSniffer.Detect([0xFF]));
        Assert.Equal(MediaFormat.Binary, MediaSniffer.Detect([0xFF, 0xD8]));
    }

    [Fact]
    public void ByteSizeParsesTheUsualSuffixes()
    {
        Assert.True(ByteSize.TryParse("512", out var plain));
        Assert.Equal(512, plain);

        Assert.True(ByteSize.TryParse("4KB", out var kb));
        Assert.Equal(4096, kb);

        Assert.True(ByteSize.TryParse("2 MB", out var mb));
        Assert.Equal(2 * 1024 * 1024, mb);

        Assert.True(ByteSize.TryParse("1g", out var gb));
        Assert.Equal(1024L * 1024 * 1024, gb);

        Assert.False(ByteSize.TryParse("banana", out _));
        Assert.Equal("1.5 MB", ByteSize.Format(1_572_864));
    }

    [Fact]
    public void TdfContainerDetectsATamperedPayload()
    {
        var payload = new byte[] { 0, 0, 0, 1, 42, 0, 0, 0, 2 };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var container = TdfContainer.Write(1234, payload);
            File.WriteAllBytes(path, container);

            var read = TdfContainer.Read(path);
            Assert.Equal(1234, read.Version);
            Assert.Equal(payload, read.Payload);

            container[10] ^= 0xFF;
            File.WriteAllBytes(path, container);
            Assert.Throws<InvalidDataException>(() => TdfContainer.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void QtDataStreamReadsBigEndianByteArrays()
    {
        var stream = new byte[] { 0, 0, 0, 3, 1, 2, 3, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 1, 9 };
        var reader = new QtDataStreamReader(stream);

        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadByteArray());
        Assert.Empty(reader.ReadByteArray());   // 0xFFFFFFFF means null
        Assert.Equal(new byte[] { 9 }, reader.ReadByteArray());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void QtDataStreamRejectsATruncatedArray()
    {
        // A ref struct cannot be captured by a lambda or a method group, so the
        // expected exception is checked by hand.
        var truncated = new QtDataStreamReader([0, 0, 0, 10, 1, 2]);
        try
        {
            _ = truncated.ReadByteArray();
            Assert.Fail("expected an InvalidDataException for a truncated QByteArray");
        }
        catch (InvalidDataException)
        {
            // expected
        }
    }

    private static byte[] BuildRiff(string form)
    {
        var result = new byte[16];
        "RIFF"u8.CopyTo(result.AsSpan(0, 4));
        System.Text.Encoding.ASCII.GetBytes(form).CopyTo(result.AsSpan(8, 4));
        return result;
    }

    private static byte[] BuildIso(string brand)
    {
        var result = new byte[16];
        "ftyp"u8.CopyTo(result.AsSpan(4, 4));
        System.Text.Encoding.ASCII.GetBytes(brand).CopyTo(result.AsSpan(8, 4));
        return result;
    }

    private static byte[] Gzip(string payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(
                   output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }
}
