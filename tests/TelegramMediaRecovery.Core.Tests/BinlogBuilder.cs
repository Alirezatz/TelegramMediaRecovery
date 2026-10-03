namespace TelegramMediaRecovery.Core.Tests;

/// <summary>
/// Builds byte-exact binlog payloads so the parser can be tested against the
/// layout the C++ structs produce.
/// </summary>
/// <remarks>
/// Every builder mirrors one struct from lib_storage
/// <c>storage/cache/storage_cache_types.h</c>, padding included, because the
/// padding is exactly what a naive parser gets wrong.
/// </remarks>
internal static class BinlogBuilder
{
    public const int StoreSize = 64;
    public const int StoreWithTimeSize = 80;
    public const int MultiHeadSize = 16;
    public const int KeySize = 16;

    private const int PlaceOffset = 32;
    private const int ChecksumOffset = 40;
    private const int KeyOffset = 48;

    /// <summary>16-byte <c>BasicHeader</c>; flag bit 0 marks timestamped records.</summary>
    public static byte[] Header(bool trackEstimatedTime, uint systemTime = 1_700_000_000)
    {
        var header = new byte[CacheBasicHeader.Size];
        header[0] = 0x00;                                    // format = Format_0
        if (trackEstimatedTime)
        {
            header[1] = 0x01;                                // kTrackEstimatedTime
        }

        BitConverter.TryWriteBytes(header.AsSpan(4, 4), systemTime);
        return header;
    }

    /// <summary>One <c>StoreWithTime</c> record.</summary>
    public static byte[] StoreWithTime(
        CacheKey key,
        byte tag,
        int size,
        uint checksum,
        PlaceId place,
        uint systemTime,
        ulong relative)
    {
        var record = new byte[StoreWithTimeSize];
        record[0] = 0x01;                                    // Store::kType
        record[1] = tag;
        // +2 .. +8 is padding
        WriteEntrySize(record, size);                        // +8
        place.Bytes.CopyTo(record.AsSpan(PlaceOffset));      // +32
        BitConverter.TryWriteBytes(record.AsSpan(ChecksumOffset, 4), checksum);
        WriteKey(record, key);
        BitConverter.TryWriteBytes(record.AsSpan(64, 4), (uint)relative);
        BitConverter.TryWriteBytes(record.AsSpan(68, 4), (uint)(relative >> 32));
        BitConverter.TryWriteBytes(record.AsSpan(72, 4), systemTime);
        return record;
    }

    /// <summary>One <c>Store</c> record (no timestamps).</summary>
    public static byte[] Store(CacheKey key, byte tag, int size, uint checksum, PlaceId place)
    {
        var record = new byte[StoreSize];
        record[0] = 0x01;
        record[1] = tag;
        WriteEntrySize(record, size);
        place.Bytes.CopyTo(record.AsSpan(PlaceOffset));
        BitConverter.TryWriteBytes(record.AsSpan(ChecksumOffset, 4), checksum);
        WriteKey(record, key);
        return record;
    }

    /// <summary>A <c>MultiRemove</c> head followed by its keys.</summary>
    public static byte[] MultiRemove(params CacheKey[] keys)
    {
        var record = new byte[MultiHeadSize + (keys.Length * KeySize)];
        record[0] = 0x03;
        record[1] = (byte)keys.Length;

        for (var i = 0; i < keys.Length; i++)
        {
            var offset = MultiHeadSize + (i * KeySize);
            BitConverter.TryWriteBytes(record.AsSpan(offset, 8), keys[i].High);
            BitConverter.TryWriteBytes(record.AsSpan(offset + 8, 8), keys[i].Low);
        }

        return record;
    }

    /// <summary>A <c>MultiAccess</c> head followed by its keys.</summary>
    public static byte[] MultiAccess(ulong relative, uint systemTime, params CacheKey[] keys)
    {
        var record = new byte[MultiHeadSize + (keys.Length * KeySize)];
        record[0] = 0x04;
        record[1] = (byte)keys.Length;
        BitConverter.TryWriteBytes(record.AsSpan(4, 4), (uint)relative);
        BitConverter.TryWriteBytes(record.AsSpan(8, 4), (uint)(relative >> 32));
        BitConverter.TryWriteBytes(record.AsSpan(12, 4), systemTime);

        for (var i = 0; i < keys.Length; i++)
        {
            var offset = MultiHeadSize + (i * KeySize);
            BitConverter.TryWriteBytes(record.AsSpan(offset, 8), keys[i].High);
            BitConverter.TryWriteBytes(record.AsSpan(offset + 8, 8), keys[i].Low);
        }

        return record;
    }

    /// <summary>A <c>MultiStore</c> head followed by its parts.</summary>
    public static byte[] MultiStore(bool withTime, params byte[][] parts)
    {
        var partSize = withTime ? StoreWithTimeSize : StoreSize;
        var record = new byte[MultiHeadSize + (parts.Length * partSize)];
        record[0] = 0x02;
        record[1] = (byte)parts.Length;

        for (var i = 0; i < parts.Length; i++)
        {
            parts[i].CopyTo(record, MultiHeadSize + (i * partSize));
        }

        return record;
    }

    private static void WriteKey(byte[] record, CacheKey key)
    {
        BitConverter.TryWriteBytes(record.AsSpan(KeyOffset, 8), key.High);
        BitConverter.TryWriteBytes(record.AsSpan(KeyOffset + 8, 8), key.Low);
    }

    /// <summary>
    /// Fills <c>EntrySize</c> the way <c>Store::setSize()</c> does: only the first
    /// byte of each of the three <c>uint64</c> members carries information.
    /// </summary>
    private static void WriteEntrySize(byte[] record, int size)
    {
        record[8] = (byte)(size & 0xFF);
        record[16] = (byte)((size >> 8) & 0xFF);
        record[24] = (byte)((size >> 16) & 0xFF);
    }
}
