using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using TelegramMediaRecovery.Core.Browsing;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Core.Tests;

public sealed class StreamingMemoryTests
{
    [Fact]
    public void PlansRetainOnlyRangesNotDecryptedValues()
    {
        var references = new List<WeakReference<byte[]>>();
        var entries = Enumerable.Range(1, 8).Select(n => new CacheEntry(new CacheKey(0x10000, 0x10000UL + (ulong)n),
            PlaceId.Empty, StreamingLayout.InSlice, 0, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0)).ToArray();
        var plan = StreamedFilePlanner.BuildPlan(entries, _ =>
        {
            var bytes = new byte[StreamingLayout.InSlice];
            references.Add(new WeakReference<byte[]>(bytes));
            return bytes;
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(references, weak => Assert.False(weak.TryGetTarget(out _)));
        Assert.Equal(64L * 1024 * 1024, plan.CachedBytes);
        Assert.Equal(512, plan.Ranges.Count);
        GC.KeepAlive(plan);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(65567)]
    public void BatchedCtrMatchesIndependentEcbCounterEncryption(int length)
    {
        var random = new Random(42);
        var key = new byte[32]; random.NextBytes(key);
        var iv = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFE");
        var input = new byte[length]; random.NextBytes(input);
        var counters = new byte[(length + 15) / 16 * 16];
        var initial = new BigInteger(iv, isUnsigned: true, isBigEndian: true) + 19;
        for (var offset = 0; offset < counters.Length; offset += 16)
        {
            var encoded = ((initial + offset / 16) % (BigInteger.One << 128)).ToByteArray(isUnsigned: true, isBigEndian: true);
            encoded.CopyTo(counters, offset + 16 - encoded.Length);
        }
        using var aes = Aes.Create();
        aes.Key = key;
        var stream = aes.EncryptEcb(counters, PaddingMode.None);
        var expected = input.Select((b, i) => (byte)(b ^ stream[i])).ToArray();
        using var cipher = new AesCtrCipher(key, iv);
        cipher.Transform(input, 19);
        Assert.Equal(expected, input);
    }

    [Fact]
    public void OnePassExportPreservesOverlapPriorityAndHashesTheActualBytes()
    {
        using var fixture = new RecoveryFixture();
        var body = new byte[StreamingLayout.InSlice];
        body.AsSpan().Fill(22);
        RecoveryFixture.Wave(11).CopyTo(body, 0);
        using var map = new MemoryStream();
        using (var writer = new BinaryWriter(map, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(2u);
            writer.Write((uint)(64 * 1024)); writer.Write(128 * 1024);
            writer.Write(Enumerable.Repeat((byte)33, 128 * 1024).ToArray());
            writer.Write((uint)(128 * 1024)); writer.Write(128 * 1024);
            writer.Write(Enumerable.Repeat((byte)11, 128 * 1024).ToArray());
        }
        fixture.Add("media_cache", new CacheKey(0x10000, 0x10000), map.ToArray());
        fixture.Add("media_cache", new CacheKey(0x10000, 0x10001), body);
        fixture.Commit();
        using var session = new RecoverySession(fixture.Source);
        var item = Assert.Single(session.Scan().Items);
        var written = Assert.Single(session.Export([item], fixture.Output).Report.Items);
        var expected = (byte[])body.Clone();
        expected.AsSpan(128 * 1024, 64 * 1024).Fill(33);
        expected.AsSpan(192 * 1024, 64 * 1024).Fill(11);
        Assert.Equal(expected, File.ReadAllBytes(written.OutputPath!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), written.Sha256);
        Assert.Equal(1, written.Coverage);
    }

    [Fact]
    public void WriterCancellationRemovesOnlyItsOwnFile()
    {
        using var fixture = new RecoveryFixture();
        var bytes = RecoveryFixture.Bitmap(11);
        var entry = new CacheEntry(new CacheKey(1, 0), PlaceId.Empty, bytes.Length, 0, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);
        var plan = StreamedFilePlanner.BuildPlan([entry], _ => bytes);
        var existing = Path.Combine(fixture.Root, "existing.bmp");
        File.WriteAllBytes(existing, [9, 8, 7]);
        Assert.Throws<IOException>(() => StreamedFileWriter.Write(plan, existing));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(existing));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var incomplete = Path.Combine(fixture.Root, "cancelled.bmp");
        Assert.Throws<OperationCanceledException>(() => StreamedFileWriter.Write(plan, incomplete, cancelled.Token));
        Assert.False(File.Exists(incomplete));
    }
}
