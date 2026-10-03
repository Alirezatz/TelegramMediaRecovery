using System.Buffers.Binary;

namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// Native (x86-64, little-endian) memory layout of the binlog record structs.
/// </summary>
/// <remarks>
/// Every offset here was derived from the C++ definitions in lib_storage
/// <c>storage/cache/storage_cache_types.h</c> by applying the MSVC/Itanium
/// alignment rules that the build uses. They are asserted against real binlogs
/// by the unit tests, and the most important one &mdash; that <c>EntrySize</c> is
/// <c>std::array&lt;uint64, 3&gt;</c> and therefore <b>24</b> bytes wide rather
/// than 3 &mdash; is what makes the difference between parsing 86 085 entries and
/// parsing none.
/// <code>
/// struct Store {                    // sizeof == 64
///     uint8      type;              // +0
///     uint8      tag;               // +1
///     // 6 bytes padding
///     EntrySize  size;              // +8   array&lt;uint64,3&gt; == 24 bytes
///     PlaceId    place;             // +32  array&lt;uint8,7&gt;
///     // 1 byte padding
///     uint32     checksum;          // +40
///     // 4 bytes padding
///     Key        key;               // +48  { uint64 high; uint64 low; }
/// };
///
/// struct StoreWithTime : Store {    // sizeof == 80
///     EstimatedTimePoint time;      // +64  { uint32 relative1, relative2, system; }
///     uint32 reserved;              // +76
/// };
///
/// struct MultiStore {               // sizeof(head) == 16
///     uint8 type; uint8 count[3]; uint32 reserved1, reserved2, reserved3;
/// } then count * Store(WithTime)
///
/// struct MultiRemove { ... }        // 16-byte head then count * Key
/// struct MultiAccess  { uint8 type; uint8 count[3]; EstimatedTimePoint time; }
///                                  // 16-byte head then count * Key
/// </code>
/// </remarks>
public static class CacheRecordLayout
{
    /// <summary>Size of <c>Store</c>.</summary>
    public const int Store = 64;

    /// <summary>Size of <c>StoreWithTime</c>.</summary>
    public const int StoreWithTime = 80;

    /// <summary>Size of the head of every <c>Multi*</c> record.</summary>
    public const int MultiHead = 16;

    /// <summary>Size of a <c>Key</c> part.</summary>
    public const int Key = 16;

    // --- Store field offsets -------------------------------------------------
    private const int TypeOffset = 0;
    private const int TagOffset = 1;
    private const int EntrySizeOffset = 8;
    private const int PlaceOffset = 32;
    private const int ChecksumOffset = 40;
    private const int KeyOffset = 48;

    // --- StoreWithTime field offsets ----------------------------------------
    private const int RelativeLowOffset = 64;
    private const int RelativeHighOffset = 68;
    private const int SystemTimeOffset = 72;

    /// <summary>Maximum number of parts a <c>Multi*</c> record may declare (<c>kBundledRecordsLimit</c>).</summary>
    public const int MaxBundledRecords = 1 << 24;

    /// <summary>
    /// Hard upper bound for a single cached value: <c>kDataSizeLimit = 8 &lt;&lt; 24</c>,
    /// i.e. 128 MiB.
    /// </summary>
    public const int MaxEntrySize = 8 << 24;

    /// <summary>Reads the record type byte.</summary>
    public static CacheRecordType TypeOf(ReadOnlySpan<byte> record) => (CacheRecordType)record[TypeOffset];

    /// <summary>Reads the opaque tag byte.</summary>
    public static byte TagOf(ReadOnlySpan<byte> record) => record[TagOffset];

    /// <summary>
    /// Decodes <c>EntrySize</c> exactly as <c>Store::getSize()</c> does.
    /// </summary>
    /// <remarks>
    /// <c>ReadTo</c> fills <c>element[0..2]</c> with the successive bytes of the
    /// value, so each of the three <c>uint64</c> members only ever holds one
    /// meaningful byte, and <c>ReadFrom</c> folds them back together most
    /// significant-last: <c>(e2 &lt;&lt; 16) | (e1 &lt;&lt; 8) | e0</c>.
    /// </remarks>
    public static int EntrySizeOf(ReadOnlySpan<byte> record)
    {
        var e0 = record[EntrySizeOffset];
        var e1 = record[EntrySizeOffset + 8];
        var e2 = record[EntrySizeOffset + 16];
        return (e2 << 16) | (e1 << 8) | e0;
    }

    /// <summary>Reads the 7-byte place id.</summary>
    public static byte[] PlaceOf(ReadOnlySpan<byte> record) =>
        record.Slice(PlaceOffset, Cache.PlaceId.Size).ToArray();

    /// <summary>Reads <c>XXH32(value, 0)</c>.</summary>
    public static uint ChecksumOf(ReadOnlySpan<byte> record) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record[ChecksumOffset..]);

    /// <summary>Reads the 16-byte key.</summary>
    public static CacheKey KeyOf(ReadOnlySpan<byte> record) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(record[KeyOffset..]),
        BinaryPrimitives.ReadUInt64LittleEndian(record[(KeyOffset + 8)..]));

    /// <summary>Reads the 16-byte key of a <c>Multi*</c> part.</summary>
    public static CacheKey PartKeyOf(ReadOnlySpan<byte> part) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(part),
        BinaryPrimitives.ReadUInt64LittleEndian(part[8..]));

    /// <summary>Reads the <c>RecordsCount</c> of a <c>Multi*</c> head.</summary>
    public static int MultiCountOf(ReadOnlySpan<byte> head) =>
        head[1] | (head[2] << 8) | (head[3] << 16);

    /// <summary>Reads <c>EstimatedTimePoint::getRelative()</c> from a <c>StoreWithTime</c>.</summary>
    public static ulong RelativeOf(ReadOnlySpan<byte> record) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record[RelativeLowOffset..])
        | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(record[(RelativeHighOffset)..]) << 32);

    /// <summary>Reads <c>EstimatedTimePoint::system</c> from a <c>StoreWithTime</c>.</summary>
    public static uint SystemTimeOf(ReadOnlySpan<byte> record) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record[SystemTimeOffset..]);

    /// <summary>Reads the 12-byte <c>EstimatedTimePoint</c> of a <c>MultiAccess</c> head.</summary>
    public static (ulong Relative, uint SystemTime) MultiAccessTimeOf(ReadOnlySpan<byte> head) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(head[4..])
            | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) << 32),
         BinaryPrimitives.ReadUInt32LittleEndian(head[12..]));
}
