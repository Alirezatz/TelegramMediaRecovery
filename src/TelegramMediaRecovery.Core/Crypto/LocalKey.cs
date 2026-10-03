namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// The 256-byte (or 64-byte) "local key" that unlocks every TDEF container in a
/// <c>tdata</c> folder.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/storage_encryption.h</c> &rarr;
/// <c>EncryptionKey::kSize = 256</c> and <c>kSize_v2 = 64</c>. Both sizes are
/// accepted because a folder may have been created by an older build.
/// </remarks>
public sealed class LocalKey
{
    /// <summary>Size of the classic local key.</summary>
    public const int LegacyKeySize = 256;

    /// <summary>Size of the newer ("v2") local key.</summary>
    public const int ShortKeySize = 64;

    private readonly byte[] _bytes;

    public LocalKey(byte[] bytes)
    {
        if (bytes.Length is not (LegacyKeySize or ShortKeySize))
        {
            throw new ArgumentException(
                $"A local key must be {LegacyKeySize} or {ShortKeySize} bytes, got {bytes.Length}.",
                nameof(bytes));
        }

        _bytes = bytes;
    }

    /// <summary>Number of bytes in the key.</summary>
    public int Length => _bytes.Length;

    /// <summary>Read-only view of the raw key material.</summary>
    public ReadOnlySpan<byte> Span => _bytes;

    /// <summary>Returns a defensive copy of the key material.</summary>
    public byte[] ToArray() => (byte[])_bytes.Clone();

    /// <inheritdoc />
    public override string ToString() => $"LocalKey({Length} bytes)";
}
