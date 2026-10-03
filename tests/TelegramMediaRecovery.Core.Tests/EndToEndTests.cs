
namespace TelegramMediaRecovery.Core.Tests;

/// <summary>
/// End-to-end test over a synthetic <c>tdata</c> folder this project builds
/// itself: the container writer the client uses is re-implemented on the write
/// side, then the whole read path has to give the original bytes back.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "tmr-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private LocalKey _key = null!;

    [Fact]
    public void ARoundTripFolderYieldsEveryValue()
    {
        _key = new LocalKey(RandomBytes(256));
        var photos = new List<byte[]> { Jpeg(4096), Png(2048), Jpeg(1024) };
        var video = Mp4(3 * 1024 * 1024);

        WriteTdata(photos, video);

        var output = Path.Combine(_root, "out");
        var report = new ExtractionEngine(new ExtractionOptions
        {
            OutputDirectory = output,
            ScanOrphans = true,
            WriteHtmlReport = true,
            WriteJsonManifest = true,
        }).Run(_root, _key);

        Assert.Equal(2, report.Databases.Count);

        var recovered = report.Items
            .Select(static i => File.ReadAllBytes(i.OutputPath!))
            .ToList();

        foreach (var photo in photos)
        {
            Assert.True(
                recovered.Any(candidate => candidate.AsSpan().SequenceEqual(photo)),
                $"a photo was lost; recovered heads: "
                    + string.Join(", ", recovered.Select(static r => MediaSniffer.Describe(r))));
        }

        Assert.Contains(recovered, candidate => candidate.AsSpan().SequenceEqual(video));

        // The manifest is the contract users script against.
        var csv = File.ReadAllLines(Path.Combine(output, "manifest.csv"));
        Assert.Equal(Reporting.CsvManifestWriter.Header, csv[0]);
        Assert.Equal(report.Items.Count + 1, csv.Length);

        // The HTML gallery must be self-contained: no scripts, no remote assets.
        var html = File.ReadAllText(Path.Combine(output, "index.html"));
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//cdn", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACorruptedPlaceFileIsRejectedInsteadOfWrittenOut()
    {
        _key = new LocalKey(RandomBytes(256));
        WriteTdata([Jpeg(2048)], null);

        var versionPath = Path.Combine(_root, "user_data", "cache", "0");
        var victim = Directory.GetFiles(versionPath, "*", SearchOption.AllDirectories)
            .First(f => !Path.GetFileName(f).StartsWith("binlog", StringComparison.Ordinal));

        var bytes = File.ReadAllBytes(victim);
        bytes[70] ^= 0xFF;                    // inside the encrypted TDEF header
        File.WriteAllBytes(victim, bytes);

        var report = new ExtractionEngine(new ExtractionOptions
        {
            OutputDirectory = Path.Combine(_root, "out2"),
            ScanOrphans = false,
        }).Run(_root, _key);

        Assert.Equal(0, report.Counters.FilesWritten);
    }

    [Fact]
    public void AnAccountFilterLimitsWhatIsTouched()
    {
        _key = new LocalKey(RandomBytes(256));
        WriteTdata([Jpeg(2048)], null);
        WriteTdata([Jpeg(4096)], null, account: "user_data#2");

        var report = new ExtractionEngine(new ExtractionOptions
        {
            OutputDirectory = Path.Combine(_root, "out3"),
            Accounts = ["user_data#2"],
            ScanOrphans = false,
        }).Run(_root, _key);

        Assert.All(report.Items, static item => Assert.Equal("user_data#2", item.Account));
        Assert.Single(report.Items);
    }

    [Fact]
    public void DuplicatesAreSuppressedByContentHash()
    {
        _key = new LocalKey(RandomBytes(256));
        var duplicate = Jpeg(3000);
        WriteTdata([duplicate, duplicate], null);

        var report = new ExtractionEngine(new ExtractionOptions
        {
            OutputDirectory = Path.Combine(_root, "out4"),
            ScanOrphans = false,
        }).Run(_root, _key);

        Assert.Single(report.Items);
        Assert.Equal(1, report.Counters.Duplicates);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Lays out a folder shaped like a real one: a <c>key_datas</c> container and
    /// <c>cache</c> + <c>media_cache</c> databases, each with a version folder, a
    /// binlog and place files.
    /// </summary>
    private void WriteTdata(IReadOnlyList<byte[]> photos, byte[]? video, string account = "user_data")
    {
        Directory.CreateDirectory(_root);
        WriteKeyDatas();

        WriteCache(Path.Combine(_root, account, "cache"), photos);
        WriteMediaCache(Path.Combine(_root, account, "media_cache"), video);
    }

    private void WriteKeyDatas()
    {
        var salt = RandomBytes(32);
        var keyBlob = LocalBlobCipher.Encrypt(_key.Span, PasscodeKeyDerivation.DeriveModern(string.Empty, salt));

        var payload = new List<byte>();
        payload.AddRange(BigEndianArray(salt));
        payload.AddRange(BigEndianArray(keyBlob));
        payload.AddRange(BigEndianArray(RandomBytes(64)));

        File.WriteAllBytes(Path.Combine(_root, "key_datas"), TdfContainer.Write(1_002_000, [.. payload]));
    }

    private void WriteCache(string databasePath, IReadOnlyList<byte[]> photos)
    {
        var versionPath = CreateVersion(databasePath);
        var binlog = new List<byte>(BinlogBuilder.Header(trackEstimatedTime: true));

        for (var i = 0; i < photos.Count; i++)
        {
            var place = NewPlace(i + 1);
            WritePlaceFile(versionPath, place, photos[i]);
            binlog.AddRange(BinlogBuilder.StoreWithTime(
                new CacheKey(0x100 + (ulong)i, 0x2000 + (ulong)i),
                1,
                photos[i].Length,
                XxHash32.Compute(photos[i]),
                place,
                1_700_000_000,
                1_000 + (ulong)i));
        }

        WriteBinlog(versionPath, [.. binlog]);
    }

    private void WriteMediaCache(string databasePath, byte[]? video)
    {
        var versionPath = CreateVersion(databasePath);
        var binlog = new List<byte>(BinlogBuilder.Header(trackEstimatedTime: true));

        if (video is not null)
        {
            var place = NewPlace(99);
            WritePlaceFile(versionPath, place, video);
            binlog.AddRange(BinlogBuilder.StoreWithTime(
                new CacheKey(0x777, 0x888),
                5,
                video.Length,
                XxHash32.Compute(video),
                place,
                1_700_000_000,
                2_000));
        }

        WriteBinlog(versionPath, [.. binlog]);
    }

    private static string CreateVersion(string databasePath)
    {
        var versionPath = Path.Combine(databasePath, "0");
        Directory.CreateDirectory(versionPath);
        return versionPath;
    }

    private void WriteBinlog(string versionPath, byte[] payload) =>
        File.WriteAllBytes(Path.Combine(versionPath, "binlog"), WriteTdefContainer(payload));

    private static PlaceId NewPlace(int seed)
    {
        var bytes = new byte[PlaceId.Size];
        Random.Shared.NextBytes(bytes);
        bytes[0] = (byte)seed;
        return new PlaceId(bytes);
    }

    /// <summary>Writes a TDEF container the way <c>Storage::File</c> does.</summary>
    private byte[] WriteTdefContainer(byte[] payload)
    {
        var padded = ((payload.Length + 15) / 16) * 16;
        var plain = new byte[padded];
        payload.CopyTo(plain, 0);
        Random.Shared.NextBytes(plain.AsSpan(payload.Length));

        var salt = RandomBytes(64);
        var crypto = new StorageEncryptionKey(_key);

        var header = new byte[48];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), 0);            // format
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), 0);            // reserved
        BitConverter.TryWriteBytes(header.AsSpan(8, 8), 1_002_000L);    // appVersion
        System.Security.Cryptography.SHA256.HashData([.. _key.Span, .. salt, .. header.AsSpan(0, 16)])
            .CopyTo(header, 16);

        var result = new List<byte>();
        result.AddRange("TDEF"u8.ToArray());
        result.AddRange(salt);
        result.AddRange(Crypt(crypto, salt, blockIndex: 0, header));
        result.AddRange(Crypt(crypto, salt, blockIndex: 3, plain));
        return [.. result];
    }

    private static byte[] Crypt(StorageEncryptionKey crypto, byte[] salt, long blockIndex, byte[] data)
    {
        var copy = (byte[])data.Clone();
        using var cipher = new AesCtrCipher(crypto.DeriveCtrKey(salt), crypto.DeriveCtrIv(salt));
        cipher.Transform(copy, blockIndex);
        return copy;
    }

    private void WritePlaceFile(string versionPath, PlaceId place, byte[] value)
    {
        var path = Path.Combine(
            versionPath,
            place.ToRelativePath().Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, WriteTdefContainer(value));
    }

    // ------------------------------------------------------------------- data

    private static byte[] Jpeg(int length)
    {
        var data = new byte[length];
        data[0] = 0xFF;
        data[1] = 0xD8;
        data[2] = 0xFF;
        data[3] = 0xE0;
        Random.Shared.NextBytes(data.AsSpan(4));
        return data;
    }

    private static byte[] Png(int length)
    {
        var data = new byte[length];
        // The full eight-byte PNG signature, not just the first half.
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(data, 0);
        Random.Shared.NextBytes(data.AsSpan(signature.Length));
        return data;
    }

    private static byte[] Mp4(int length)
    {
        var data = new byte[length];
        "ftypisom"u8.CopyTo(data.AsSpan(4));
        Random.Shared.NextBytes(data.AsSpan(8));
        return data;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static byte[] BigEndianArray(byte[] value)
    {
        var result = new byte[4 + value.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), (uint)value.Length);
        value.CopyTo(result, 4);
        return result;
    }
}
