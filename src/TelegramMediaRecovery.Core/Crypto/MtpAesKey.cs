using System.Security.Cryptography;

namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// Legacy MTProto key schedule used to unwrap the local key: four SHA1 digests
/// whose slices form the AES-256-IGE key and IV for a given <c>msgKey</c>.
/// </summary>
/// <remarks>
/// Reference: <c>Telegram/SourceFiles/mtproto/mtproto_auth_key.cpp</c> &rarr;
/// <c>MTP::AuthKey::prepareAES_oldmtp(key, msgKey, send = false)</c>:
/// <code>
/// sha1_a = SHA1( msgKey  | K[  8: 40] )
/// sha1_b = SHA1( K[40: 56] | msgKey | K[ 56: 72] )
/// sha1_c = SHA1( K[ 72:104] | msgKey )
/// sha1_d = SHA1( msgKey | K[104:136] )
/// aesKey = sha1_a[0:8] || sha1_b[8:20] || sha1_c[4:16]
/// aesIv  = sha1_a[8:20] || sha1_b[0:8] || sha1_c[16:20] || sha1_d[0:8]
/// </code>
/// </remarks>
public static class MtpAesKey
{
    /// <summary>Minimum key length required by the schedule.</summary>
    public const int RequiredKeySize = 136;

    /// <summary>Derives the AES-256 key and the 32-byte IGE IV for one blob.</summary>
    public static (byte[] Key, byte[] Iv) Derive(ReadOnlySpan<byte> msgKey, ReadOnlySpan<byte> localKey)
    {
        if (localKey.Length < RequiredKeySize)
        {
            throw new ArgumentException(
                $"prepareAES_oldmtp needs at least {RequiredKeySize} key bytes, got {localKey.Length}.",
                nameof(localKey));
        }

        var a = SHA1.HashData(Concat2(msgKey, localKey.Slice(8, 32)));
        var b = SHA1.HashData(Concat4(localKey.Slice(40, 16), msgKey, localKey.Slice(56, 16), default));
        var c = SHA1.HashData(Concat2(localKey.Slice(72, 32), msgKey));
        var d = SHA1.HashData(Concat2(msgKey, localKey.Slice(104, 32)));

        var key = Concat3(a.AsSpan(0, 8), b.AsSpan(8, 12), c.AsSpan(4, 12));
        var iv = Concat4(a.AsSpan(8, 12), b.AsSpan(0, 8), c.AsSpan(16, 4), d.AsSpan(0, 8));
        return (key, iv);
    }

    private static byte[] Concat2(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }

    private static byte[] Concat3(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }

    private static byte[] Concat4(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        ReadOnlySpan<byte> c,
        ReadOnlySpan<byte> d)
    {
        var result = new byte[a.Length + b.Length + c.Length + d.Length];
        var offset = 0;
        a.CopyTo(result.AsSpan(offset));
        offset += a.Length;
        b.CopyTo(result.AsSpan(offset));
        offset += b.Length;
        c.CopyTo(result.AsSpan(offset));
        offset += c.Length;
        d.CopyTo(result.AsSpan(offset));
        return result;
    }
}
