using System.Security.Cryptography;
using System.Text;

namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// Unwraps one <c>[msgKey][AES-256-IGE ciphertext]</c> blob into its plaintext
/// payload.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/details/storage_file_utilities.cpp</c> &rarr;
/// <c>DecryptLocal()</c>:
/// <list type="number">
///   <item><description>split off the 16-byte <c>msgKey</c>;</description></item>
///   <item><description>
///     derive AES key/IV with <c>MTP::AuthKey::prepareAES_oldmtp()</c>
///     (<see cref="MtpAesKey.Derive"/>);
///   </description></item>
///   <item><description>AES-256-IGE decrypt the remainder;</description></item>
///   <item><description>verify <c>SHA1(plaintext)[0..16] == msgKey</c>;</description></item>
///   <item><description>
///     the plaintext starts with a little-endian <c>uint32</c> total length
///     followed by the payload; the tail is random padding to 16 bytes.
///   </description></item>
/// </list>
/// </remarks>
public static class LocalBlobCipher
{
    /// <summary>Length of the SHA1-based message key prefix.</summary>
    public const int MsgKeySize = 16;

    /// <summary>
    /// Raised when the SHA1 message-key check fails, i.e. the passcode is wrong.
    /// </summary>
    public sealed class WrongPasscodeException : Exception
    {
        public WrongPasscodeException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Decrypts a blob and returns the payload without the length prefix.</summary>
    /// <exception cref="WrongPasscodeException">The passcode does not match.</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> localKeyBytes)
    {
        if (blob.Length <= MsgKeySize || (blob.Length % 16) != 0)
        {
            throw new ArgumentException(
                $"An encrypted blob must be 16 + n*16 bytes, got {blob.Length}.",
                nameof(blob));
        }

        var msgKey = blob[..MsgKeySize];
        var ciphertext = blob[MsgKeySize..];

        var (aesKey, aesIv) = MtpAesKey.Derive(msgKey, localKeyBytes);
        var plain = AesIge.Decrypt(ciphertext, aesKey, aesIv);

        var digest = SHA1.HashData(plain);
        if (!digest.AsSpan(0, MsgKeySize).SequenceEqual(msgKey))
        {
            throw new WrongPasscodeException(
                "SHA1 message-key mismatch: wrong passcode, or the blob is corrupt.");
        }

        var declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(plain);
        var padding = plain.Length - declared;
        if (declared < 4 || declared > plain.Length || padding >= 16)
        {
            throw new CryptographicException(
                $"Decrypted blob has an implausible length field ({declared} of {plain.Length} bytes).");
        }

        return plain[4..(int)declared];
    }

    /// <summary>
    /// Produces a blob in the exact layout <c>DecryptLocal()</c> expects. Used by
    /// the tests and by the round-trip self check; the tool itself never writes.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> localKeyBytes)
    {
        var padded = ((payload.Length + 4) + 15) & ~15;
        var plain = new byte[padded];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(plain, (uint)padded);
        payload.CopyTo(plain.AsSpan(4));
        RandomNumberGenerator.Fill(plain.AsSpan(4 + payload.Length));

        var msgKey = SHA1.HashData(plain)[..MsgKeySize];
        var (aesKey, aesIv) = MtpAesKey.Derive(msgKey, localKeyBytes);
        var ciphertext = AesIge.Encrypt(plain, aesKey, aesIv);

        var result = new byte[MsgKeySize + ciphertext.Length];
        msgKey.CopyTo(result, 0);
        ciphertext.CopyTo(result.AsSpan(MsgKeySize));
        return result;
    }
}
