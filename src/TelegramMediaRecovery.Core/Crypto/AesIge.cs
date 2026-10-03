using System.Security.Cryptography;

namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// AES-256 in Infinite Garble Extension (IGE) mode, built on raw ECB blocks.
/// </summary>
/// <remarks>
/// IGE exists in exactly one step of the pipeline: unwrapping the local key out of
/// <c>key_datas</c> / <c>map</c>. .NET has no IGE mode, so it is implemented here
/// the way OpenSSL's <c>AES_ige_encrypt</c> does it, with a 32-byte IV holding two
/// registers (<c>iv0</c> = mirrored previous block, <c>iv1</c> = chained previous
/// block):
/// <code>
/// decrypt: out[i] = D( in[i] ^ iv1 ) ^ iv0 ;  iv0 = in[i]  ; iv1 = out[i]
/// encrypt: out[i] = E( in[i] ^ iv0 ) ^ iv1 ;  iv0 = out[i] ; iv1 = in[i]
/// </code>
/// Reference: lib_storage <c>storage/details/storage_file_utilities.cpp</c> &rarr;
/// <c>DecryptLocal()</c>, which calls <c>MTP::aesIgeDecryptRaw()</c>
/// (<c>Telegram/SourceFiles/mtproto/mtproto_auth_key.cpp</c>). Only tiny blobs go
/// through here, so the per-block <c>TransformBlock</c> overhead does not matter.
/// </remarks>
public static class AesIge
{
    /// <summary>IGE needs two 16-byte registers, i.e. a 32-byte IV.</summary>
    public const int IvSize = 32;

    /// <summary>AES block size.</summary>
    public const int BlockSize = 16;

    /// <summary>Decrypts a payload whose length is a multiple of 16.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        var output = new byte[ciphertext.Length];
        Process(ciphertext, output, key, iv, decrypt: true);
        return output;
    }

    /// <summary>Encrypts a payload whose length is a multiple of 16.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        var output = new byte[plaintext.Length];
        Process(plaintext, output, key, iv, decrypt: false);
        return output;
    }

    private static void Process(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        bool decrypt)
    {
        if (iv.Length != IvSize)
        {
            throw new ArgumentException($"IGE needs a {IvSize}-byte IV, got {iv.Length}.", nameof(iv));
        }

        if (input.Length % BlockSize != 0)
        {
            throw new ArgumentException(
                $"IGE operates on whole blocks; length {input.Length} is not a multiple of {BlockSize}.",
                nameof(input));
        }

        var keyBytes = new byte[32];
        key.Slice(0, Math.Min(key.Length, 32)).CopyTo(keyBytes);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = keyBytes;

        // IGE genuinely needs both permutations: D() when decrypting, E() when encrypting.
        using var decBlock = aes.CreateDecryptor();
        using var encBlock = aes.CreateEncryptor();
        var block = decrypt ? decBlock : encBlock;

        var chained = new byte[BlockSize];
        var transformed = new byte[BlockSize];
        var outputBlock = new byte[BlockSize];

        var iv0 = iv.Slice(0, BlockSize).ToArray();
        var iv1 = iv.Slice(BlockSize, BlockSize).ToArray();

        for (var offset = 0; offset < input.Length; offset += BlockSize)
        {
            var current = input.Slice(offset, BlockSize);

            // XOR with the register that depends on the direction.
            Xor(current, decrypt ? iv1 : iv0, chained);
            block.TransformBlock(chained, 0, BlockSize, transformed, 0);

            if (decrypt)
            {
                Xor(transformed, iv0, outputBlock);
                current.CopyTo(iv0);
            }
            else
            {
                Xor(transformed, iv1, outputBlock);
                outputBlock.CopyTo(iv0);
                current.CopyTo(iv1);
            }

            if (decrypt)
            {
                outputBlock.CopyTo(iv1);
            }

            outputBlock.CopyTo(output.Slice(offset, BlockSize));
        }
    }

    private static void Xor(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(left[i] ^ right[i]);
        }
    }
}
