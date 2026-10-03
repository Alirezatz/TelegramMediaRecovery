namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// Pure managed implementation of the 32-bit xxHash algorithm.
/// </summary>
/// <remarks>
/// The cache database stores <c>XXH32(value, size, seed = 0)</c> next to every
/// entry and refuses to hand out a value whose checksum does not match, so this
/// is the integrity check used for every value we read.
/// <para>
/// Reference: lib_storage <c>storage/cache/storage_cache_database_object.cpp</c>
/// &rarr; <c>CountChecksum()</c> (uses <c>XXH32(data.data(), data.size(), 0)</c>).
/// </para>
/// </remarks>
public static class XxHash32
{
    private const uint Prime1 = 2654435761;
    private const uint Prime2 = 2246822519;
    private const uint Prime3 = 3266489917;
    private const uint Prime4 = 668265263;
    private const uint Prime5 = 374761393;

    /// <summary>Computes <c>XXH32(data, 0)</c>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Compute(data, 0);

    /// <summary>Computes <c>XXH32(data, seed)</c>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data, uint seed)
    {
        var length = data.Length;
        var index = 0;

        uint hash;
        if (length >= 16)
        {
            var v1 = seed + Prime1 + Prime2;
            var v2 = seed + Prime2;
            var v3 = seed;
            var v4 = seed - Prime1;

            var limit = length - 16;
            do
            {
                v1 = Round(v1, ReadUInt32(data, index));
                v2 = Round(v2, ReadUInt32(data, index + 4));
                v3 = Round(v3, ReadUInt32(data, index + 8));
                v4 = Round(v4, ReadUInt32(data, index + 12));
                index += 16;
            }
            while (index <= limit);

            hash = RotateLeft(v1, 1)
                 + RotateLeft(v2, 7)
                 + RotateLeft(v3, 12)
                 + RotateLeft(v4, 18);
        }
        else
        {
            hash = seed + Prime5;
        }

        hash += (uint)length;

        while (index <= length - 4)
        {
            hash = RotateLeft(hash + (ReadUInt32(data, index) * Prime3), 17) * Prime4;
            index += 4;
        }

        while (index < length)
        {
            hash = RotateLeft(hash + (data[index] * Prime5), 11) * Prime1;
            index++;
        }

        hash ^= hash >> 15;
        hash *= Prime2;
        hash ^= hash >> 13;
        hash *= Prime3;
        hash ^= hash >> 16;
        return hash;
    }

    private static uint Round(uint accumulator, uint input) =>
        RotateLeft(accumulator + (input * Prime2), 13) * Prime1;

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
}
