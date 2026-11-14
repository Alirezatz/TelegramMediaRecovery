using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;

namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// AES-256 counter mode (CTR) built on the raw AES block primitive.
/// </summary>
/// <remarks>
/// <para>
/// .NET exposes no CTR mode: <see cref="CipherMode"/> only has CBC, ECB, OFB, CFB
/// and CTS. CTR is nonetheless exactly what lib_storage uses for every TDEF
/// payload, via OpenSSL's <c>CRYPTO_ctr128_encrypt</c>
/// (<c>storage/storage_encryption.cpp</c> &rarr; <c>CtrState::process()</c>).
/// </para>
/// <para>
/// The trick that keeps this fast is that an ECB transform <em>ignores the IV
/// entirely</em>, which turns it into a raw AES block function. Encrypting an
/// incrementing 128-bit big-endian counter block therefore produces the keystream,
/// exactly like CTR mode requires &mdash; and byte 15 is the least significant
/// byte, which is the same wrap-around behaviour as OpenSSL.
/// </para>
/// </remarks>
public sealed class AesCtrCipher : IDisposable
{
    /// <summary>AES block size, and the counter granularity.</summary>
    public const int BlockSize = 16;

    private readonly ICryptoTransform _block;
    private readonly Aes _aes;
    private readonly byte[] _initialIv = new byte[BlockSize];
    private readonly byte[] _counter = new byte[BlockSize];
    private readonly byte[] _keystream = new byte[BlockSize];
    private bool _disposed;

    /// <summary>Creates a CTR cipher whose counter starts at <paramref name="iv"/>.</summary>
    public AesCtrCipher(byte[] key, ReadOnlySpan<byte> iv)
    {
        if (iv.Length != BlockSize)
        {
            throw new ArgumentException($"The CTR counter block must be {BlockSize} bytes.", nameof(iv));
        }

        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key;

        // CTR mode only ever needs the *forward* AES permutation: the keystream is
        // produced by encrypting the counter. Using a decryptor here would apply
        // the inverse cipher and silently yield garbage.
        _block = _aes.CreateEncryptor();
        iv.CopyTo(_initialIv);
        _initialIv.CopyTo(_counter, 0);
    }

    /// <summary>
    /// Applies the keystream to <paramref name="buffer"/> in place, starting at
    /// block <paramref name="firstBlockIndex"/>.
    /// </summary>
    public void Transform(Span<byte> buffer, long firstBlockIndex = 0, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        SetCounter(firstBlockIndex);
        if (buffer.Length < 256)
        {
            for (var offset = 0; offset < buffer.Length; offset += BlockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _block.TransformBlock(_counter, 0, BlockSize, _keystream, 0);
                IncrementCounter(_counter);
                Xor(buffer.Slice(offset, Math.Min(BlockSize, buffer.Length - offset)), _keystream);
            }
            return;
        }
        const int BatchSize = 64 * 1024;
        var counters = ArrayPool<byte>.Shared.Rent(BatchSize);
        var keystream = ArrayPool<byte>.Shared.Rent(BatchSize);
        try
        {
            for (var offset = 0; offset < buffer.Length; offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = Math.Min(BatchSize, buffer.Length - offset);
                var padded = (chunk + BlockSize - 1) & ~(BlockSize - 1);
                for (var index = 0; index < padded; index += BlockSize)
                {
                    _counter.CopyTo(counters, index);
                    IncrementCounter(_counter);
                }
                _block.TransformBlock(counters, 0, padded, keystream, 0);
                Xor(buffer.Slice(offset, chunk), keystream);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(counters, clearArray: true);
            ArrayPool<byte>.Shared.Return(keystream, clearArray: true);
        }
    }

    private static void Xor(Span<byte> destination, ReadOnlySpan<byte> keystream)
    {
        var index = 0;
        for (; index <= destination.Length - Vector<byte>.Count; index += Vector<byte>.Count)
        {
            (new Vector<byte>(destination[index..]) ^ new Vector<byte>(keystream[index..])).CopyTo(destination[index..]);
        }
        for (; index < destination.Length; index++) { destination[index] ^= keystream[index]; }
    }

    /// <summary>
    /// Rewinds the counter to <paramref name="firstBlockIndex"/> relative to the
    /// IV the cipher was created with, so a caller can read any part of the
    /// container without replaying everything before it.
    /// </summary>
    public void SetCounter(long firstBlockIndex)
    {
        AddBlockIndex(_initialIv, firstBlockIndex).CopyTo(_counter, 0);
    }

    /// <summary>
    /// Mirrors <c>CtrState::incrementedIv()</c>: a big-endian 128-bit addition
    /// that carries from byte 15 towards byte 0.
    /// </summary>
    public static byte[] AddBlockIndex(byte[] counter, long blockIndex)
    {
        if (blockIndex == 0)
        {
            return counter;
        }

        var result = (byte[])counter.Clone();
        long carry = blockIndex;
        for (var i = result.Length - 1; i >= 0 && carry != 0; i--)
        {
            var sum = result[i] + (carry & 0xFF);
            result[i] = (byte)sum;
            carry = (carry >> 8) + (sum >> 8);
        }

        return result;
    }

    /// <summary>One-shot helper: decrypts (or encrypts) a block-aligned buffer.</summary>
    public static void Transform(byte[] key, ReadOnlySpan<byte> iv, byte[] buffer, long firstBlockIndex = 0)
    {
        using var cipher = new AesCtrCipher(key, iv);
        cipher.Transform(buffer, firstBlockIndex);
    }

    private static void IncrementCounter(Span<byte> counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _block.Dispose();
        _aes.Dispose();
    }
}
