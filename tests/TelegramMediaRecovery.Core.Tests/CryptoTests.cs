
namespace TelegramMediaRecovery.Core.Tests;

/// <summary>
/// Tests for the primitives the whole format depends on.
/// </summary>
public sealed class CryptoTests
{
    [Fact]
    public void XxHash32MatchesTheReferenceVectors()
    {
        // Vectors produced by xxHash's own test suite for seed 0.
        Assert.Equal(0x02CC5D05u, XxHash32.Compute([]));
        Assert.Equal(0x550D7456u, XxHash32.Compute("a"u8.ToArray()));
        Assert.Equal(0x09D5A0464u, XxHash32.Compute("as"u8.ToArray()));
        Assert.Equal(0x3D83552Bu, XxHash32.Compute("asd"u8.ToArray()));
        Assert.Equal(0x5E702C32u, XxHash32.Compute("asdf"u8.ToArray()));
        Assert.Equal(0x9A75FA0Cu, XxHash32.Compute("Call me Ishmael. Some years ago--never mind"u8.ToArray()));
    }

    [Fact]
    public void XxHash32SpansAllThreeAccumulatorLanes()
    {
        // A 64-byte input exercises the 16-byte loop (four rounds, four lanes).
        var data = new byte[64];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        var digest = XxHash32.Compute(data);
        Assert.Equal(digest, XxHash32.Compute((byte[])data.Clone()));
        Assert.NotEqual(digest, XxHash32.Compute(data.AsSpan(0, 63)));
    }

    [Fact]
    public void AesCtrMatchesTheNistSp80038AVector()
    {
        // F.5.5 CTR-AES256.Encrypt
        var key = Convert.FromHexString(
            "603DEB1015CA71BE2B73AEF0857D7781" + "1F352C073B6108D72D9810A30914DFF4");
        var iv = Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF");
        var plain = Convert.FromHexString(
            "6BC1BEE22E409F96E93D7E117393172A" + "AE2D8A571E03AC9C9EB76FAC45AF8E51"
            + "30C81C46A35CE411E5FBC1191A0A52EF" + "F69F2445DF4F9B17AD2B417BE66C3710");
        // Ciphertext for this key/counter/plaintext, with the whole 128-bit block
        // used as the big-endian counter. That is the construction Telegram
        // Desktop uses (CRYPTO_ctr128_encrypt) and therefore the one that has to
        // match bit for bit.
        const string Expected =
            "601ec313775789a5b7a7f504bbf3d228" + "f443e3ca4d62b59aca84e990cacaf5c5"
            + "2b0930daa23de94ce87017ba2d84988d" + "dfc9c58db67aada613c2dd08457941a6";

        using var cipher = new AesCtrCipher(key, iv);
        var actual = (byte[])plain.Clone();
        cipher.Transform(actual);

        Assert.Equal(Expected, Convert.ToHexString(actual).ToLowerInvariant());
    }

    [Fact]
    public void AesCtrCanStartAtAnyBlockOffset()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var iv = new byte[16];
        Random.Shared.NextBytes(iv);
        var data = new byte[256];
        Random.Shared.NextBytes(data);

        var whole = (byte[])data.Clone();
        using (var cipher = new AesCtrCipher(key, iv))
        {
            cipher.Transform(whole);
        }

        // Reading the tail alone must give the same bytes as the tail of the
        // whole-buffer decryption, which is what lets TdefFile seek.
        var tail = (byte[])data[128..].Clone();
        using (var cipher = new AesCtrCipher(key, iv))
        {
            cipher.Transform(tail, firstBlockIndex: 128 / 16);
        }

        Assert.Equal(whole[128..], tail);
    }

    [Fact]
    public void CounterIncrementCarriesFromTheLastByte()
    {
        var counter = new byte[16];
        counter[15] = 0xFE;

        var incremented = AesCtrCipher.AddBlockIndex(counter, 3);

        // 0xFE + 3 == 0x101: byte 15 becomes 0x01 and carries 1 into byte 14.
        Assert.Equal(0x01, incremented[15]);
        Assert.Equal(0x01, incremented[14]);
        Assert.Equal(0x00, incremented[13]);
        Assert.Equal(0x00, incremented[12]);
    }

    [Fact]
    public void CounterWrapAroundLeavesNoCarry()
    {
        var counter = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var incremented = AesCtrCipher.AddBlockIndex(counter, 1);
        Assert.All(incremented, b => Assert.Equal(0, b));
    }

    [Fact]
    public void AesIgeRoundTrips()
    {
        var key = new byte[32];
        var iv = new byte[32];
        Random.Shared.NextBytes(key);
        Random.Shared.NextBytes(iv);

        var plain = new byte[48];
        Random.Shared.NextBytes(plain);

        var encrypted = AesIge.Encrypt(plain, key, iv);
        Assert.NotEqual(plain, encrypted);
        Assert.Equal(plain, AesIge.Decrypt(encrypted, key, iv));
    }

    [Fact]
    public void AesIgeRejectsUnalignedInput()
    {
        Assert.Throws<ArgumentException>(() => AesIge.Decrypt(new byte[17], new byte[32], new byte[32]));
    }

    [Fact]
    public void LocalBlobCipherRoundTripsAndDetectsAWrongKey()
    {
        var localKey = new LocalKey(RandomNumber(256));
        var payload = System.Text.Encoding.UTF8.GetBytes("telegram media recovery");

        // The plaintext always ends in random padding up to the next block, so
        // the decrypted prefix is what has to match.
        var blob = LocalBlobCipher.Encrypt(payload, localKey.Span);
        var decrypted = LocalBlobCipher.Decrypt(blob, localKey.Span);
        Assert.Equal(payload, decrypted[..payload.Length]);

        var wrongKey = new LocalKey(RandomNumber(256));
        Assert.Throws<LocalBlobCipher.WrongPasscodeException>(
            () => LocalBlobCipher.Decrypt(blob, wrongKey.Span));
    }

    [Fact]
    public void PasscodeDerivationIsStableAndPasscodeSensitive()
    {
        var salt = RandomNumber(32);

        var empty = PasscodeKeyDerivation.DeriveModern(string.Empty, salt);
        Assert.Equal(empty, PasscodeKeyDerivation.DeriveModern(string.Empty, salt));
        Assert.NotEqual(empty, PasscodeKeyDerivation.DeriveModern("hunter2", salt));
        Assert.Equal(256, empty.Length);

        var legacyEmpty = PasscodeKeyDerivation.DeriveLegacy(string.Empty, salt);
        Assert.NotEqual(empty, legacyEmpty);
        Assert.Equal(256, legacyEmpty.Length);
    }

    [Fact]
    public void StorageEncryptionKeySplitsTheLocalKeyInHalf()
    {
        var keyBytes = RandomNumber(256);
        var key = new LocalKey(keyBytes);
        var salt = RandomNumber(64);
        var crypto = new StorageEncryptionKey(key);

        var ctrKey = crypto.DeriveCtrKey(salt);
        var iv = crypto.DeriveCtrIv(salt);

        Assert.Equal(32, ctrKey.Length);
        Assert.Equal(16, iv.Length);
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData([.. keyBytes[..128], .. salt[..32]]),
            ctrKey);
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData([.. keyBytes[128..], .. salt[32..]])[..16],
            iv);
    }

    private static byte[] RandomNumber(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
