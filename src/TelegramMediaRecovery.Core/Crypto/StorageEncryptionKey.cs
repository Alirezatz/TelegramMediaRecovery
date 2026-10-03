using System.Security.Cryptography;

namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// Derives the per-file AES key/IV used by the TDEF container from the local key
/// and the 64-byte per-file salt.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/storage_encryption.cpp</c> &rarr;
/// <c>EncryptionKey::prepareCtrState()</c> and <c>CtrState::incrementedIv()</c>:
/// <code>
/// ctrKey = SHA256( localKey[0 : len/2] || salt[0 : 32] )
/// ctrIV  = SHA256( localKey[len/2:]  || salt[32: 64] )[0 : 16]
/// blockIndex is added to ctrIV as a 128-bit big-endian counter
/// (byte 15 is the least significant byte).
/// </code>
/// </remarks>
public sealed class StorageEncryptionKey
{
    /// <summary>Salt length stored in clear at the start of every TDEF file.</summary>
    public const int SaltSize = 64;

    /// <summary>AES block size; also the CTR counter granularity.</summary>
    public const int BlockSize = 16;

    private readonly LocalKey _key;

    public StorageEncryptionKey(LocalKey key) => _key = key;

    /// <summary>Derives the CTR key for one file.</summary>
    public byte[] DeriveCtrKey(ReadOnlySpan<byte> salt)
    {
        ValidateSalt(salt);
        var half = _key.Length / 2;
        return SHA256.HashData(Concat(_key.Span.Slice(0, half), salt[..(SaltSize / 2)]));
    }

    /// <summary>
    /// Derives the CTR counter block for one file, optionally advanced by
    /// <paramref name="blockIndex"/> 16-byte blocks.
    /// </summary>
    public byte[] DeriveCtrIv(ReadOnlySpan<byte> salt, long blockIndex = 0)
    {
        ValidateSalt(salt);
        var half = _key.Length / 2;
        var digest = SHA256.HashData(Concat(_key.Span.Slice(half), salt[(SaltSize / 2)..]));

        var counter = new byte[BlockSize];
        digest.AsSpan(0, BlockSize).CopyTo(counter);
        return AddBlockIndex(counter, blockIndex);
    }

    /// <summary>
    /// Adds <paramref name="blockIndex"/> to a 128-bit big-endian counter block,
    /// mirroring <c>CtrState::incrementedIv</c>.
    /// </summary>
    public static byte[] AddBlockIndex(byte[] counter, long blockIndex) =>
        AesCtrCipher.AddBlockIndex(counter, blockIndex);

    private static void ValidateSalt(ReadOnlySpan<byte> salt)
    {
        if (salt.Length != SaltSize)
        {
            throw new ArgumentException($"Salt must be {SaltSize} bytes, got {salt.Length}.", nameof(salt));
        }
    }

    private static byte[] Concat(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var result = new byte[left.Length + right.Length];
        left.CopyTo(result);
        right.CopyTo(result.AsSpan(left.Length));
        return result;
    }
}
