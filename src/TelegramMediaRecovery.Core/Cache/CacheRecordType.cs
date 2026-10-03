namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// Record type byte at the start of every binlog record.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/cache/storage_cache_types.h</c> &rarr;
/// <c>Store::kType</c>, <c>MultiStore::kType</c>, <c>MultiRemove::kType</c>,
/// <c>MultiAccess::kType</c>.
/// </remarks>
public enum CacheRecordType : byte
{
    /// <summary>Not a known record.</summary>
    Unknown = 0x00,

    /// <summary>One value: <c>Store</c> or <c>StoreWithTime</c> depending on the header flag.</summary>
    Store = 0x01,

    /// <summary>A bundle of <c>Store</c> parts (written by the compactor).</summary>
    MultiStore = 0x02,

    /// <summary>A bundle of keys to erase.</summary>
    MultiRemove = 0x03,

    /// <summary>A bundle of keys whose access timestamp was refreshed.</summary>
    MultiAccess = 0x04,
}
