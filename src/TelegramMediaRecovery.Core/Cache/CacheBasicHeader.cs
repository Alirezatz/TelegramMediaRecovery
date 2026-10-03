using System.Buffers.Binary;

namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// The 16-byte header that opens every binlog.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/cache/storage_cache_types.h</c> &rarr;
/// <c>struct BasicHeader</c>, which is a C++ bitfield
/// <c>uint32 format : 8; uint32 flags : 24;</c>. On little-endian x86 that puts
/// the format in byte 0 and the flags in bytes 1..3, i.e. reading the field as a
/// little-endian <c>uint32</c> and shifting right by 8 recovers both.
/// <para>
/// The only defined flag today is <see cref="TrackEstimatedTimeFlag"/>. When it is
/// set, every <c>Store</c> record is a <c>StoreWithTime</c> (80 bytes instead of
/// 64). Getting this wrong is what makes naive binlog parsers stop at byte 16.
/// </para>
/// </remarks>
public sealed class CacheBasicHeader
{
    /// <summary>Bit 0 of <see cref="Flags"/>: records carry timestamps.</summary>
    public const uint TrackEstimatedTimeFlag = 0x01;

    /// <summary>Size of the header in bytes.</summary>
    public const int Size = 16;

    /// <summary>Only <c>Format_0</c> exists so far.</summary>
    public const uint SupportedFormat = 0;

    private CacheBasicHeader(byte format, uint flags, uint systemTime)
    {
        Format = format;
        Flags = flags;
        SystemTime = systemTime;
    }

    /// <summary>Container format version (must be 0).</summary>
    public byte Format { get; }

    /// <summary>Raw 24-bit flags.</summary>
    public uint Flags { get; }

    /// <summary>Unix time the binlog was created; the origin of its relative clock.</summary>
    public uint SystemTime { get; }

    /// <summary>True when records carry <c>EstimatedTimePoint</c> fields.</summary>
    public bool TrackEstimatedTime => (Flags & TrackEstimatedTimeFlag) != 0;

    /// <summary>Reads the header from the first bytes of a decrypted binlog.</summary>
    public static CacheBasicHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new InvalidDataException(
                $"A binlog must start with a {Size}-byte header, got {data.Length} bytes.");
        }

        var word = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return new CacheBasicHeader(
            (byte)(word & 0xFF),
            (word >> 8) & 0x00FF_FFFF,
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"format={Format} flags=0x{Flags:X6} trackTime={TrackEstimatedTime} systemTime={SystemTime}";
}
