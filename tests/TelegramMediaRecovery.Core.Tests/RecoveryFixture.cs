using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace TelegramMediaRecovery.Core.Tests;

/// <summary>Small, self-contained standard Telegram cache. Contains no real user data.</summary>
internal sealed class RecoveryFixture : IDisposable
{
    private readonly LocalKey _key = new(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
    private readonly Dictionary<string, List<(CacheKey Key, byte[] Bytes, byte Tag)>> _databases = [];
    private readonly Dictionary<CacheKey, string> _places = [];
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tmr-workflow-tests-" + Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "tdata");
    public string Output => Path.Combine(Root, "exports");

    public RecoveryFixture(string passcode = "")
    {
        Directory.CreateDirectory(Source);
        var salt = SHA256.HashData("synthetic-test-salt"u8);
        var wrapped = LocalBlobCipher.Encrypt(_key.Span, PasscodeKeyDerivation.DeriveModern(passcode, salt));
        File.WriteAllBytes(Path.Combine(Source, "key_datas"), TdfContainer.Write(1_002_000,
            [.. QtArray(salt), .. QtArray(wrapped), .. QtArray(new byte[64])]));
    }
    public void Add(string database, CacheKey key, byte[] bytes, byte tag = 1)
    {
        if (!_databases.TryGetValue(database, out var values)) { _databases[database] = values = []; }
        values.Add((key, bytes, tag));
    }
    public void Commit()
    {
        foreach (var (name, values) in _databases)
        {
            var version = Path.Combine(Source, "user_data", name, "0");
            Directory.CreateDirectory(version);
            var binlog = new List<byte>(BinlogBuilder.Header(trackEstimatedTime: true));
            for (var index = 0; index < values.Count; index++)
            {
                var (key, bytes, tag) = values[index];
                var place = new PlaceId([(byte)(index + 1), 2, 3, 4, 5, 6, 7]);
                var path = Path.Combine(version, place.ToRelativePath().Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, EncryptContainer(bytes));
                _places[key] = path;
                binlog.AddRange(BinlogBuilder.CompactStore(key, bytes.Length, XxHash32.Compute(bytes), place, tag));
            }
            File.WriteAllBytes(Path.Combine(version, "binlog"), EncryptContainer([.. binlog]));
        }
    }
    public string Place(CacheKey key) => _places[key];
    public Dictionary<string, string> SourceHashes() => Directory.GetFiles(Source, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(Source, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    public static byte[] Bitmap(byte color, int size = 70)
    {
        var bytes = new byte[size];
        "BM"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), size);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        bytes.AsSpan(54).Fill(color);
        return bytes;
    }
    public static byte[] Wave(byte value)
    {
        var bytes = Enumerable.Repeat(value, 16_384).ToArray();
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 8_000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 16_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), bytes.Length - 44);
        return bytes;
    }
    private byte[] EncryptContainer(byte[] payload)
    {
        var padded = new byte[(payload.Length + 15) / 16 * 16];
        payload.CopyTo(padded, 0);
        var salt = SHA512.HashData(payload);
        var crypto = new StorageEncryptionKey(_key);
        var header = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), 1_002_000);
        SHA256.HashData([.. _key.Span, .. salt, .. header.AsSpan(0, 16)]).CopyTo(header, 16);
        using var cipher = new AesCtrCipher(crypto.DeriveCtrKey(salt), crypto.DeriveCtrIv(salt));
        cipher.Transform(header, 0);
        cipher.Transform(padded, 3);
        return [.. "TDEF"u8, .. salt, .. header, .. padded];
    }
    private static byte[] QtArray(byte[] bytes)
    {
        var result = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(result, bytes.Length);
        bytes.CopyTo(result, 4);
        return result;
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("tmr-workflow-tests-", StringComparison.Ordinal))
        {
            Directory.Delete(full, recursive: true);
        }
    }
}
