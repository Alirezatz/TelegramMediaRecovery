namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// One live entry of a cache database, i.e. everything a <c>Store</c> record
/// tells us about a single cached value.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/cache/storage_cache_database_object.cpp</c>
/// &rarr; <c>processRecordStoreGeneric()</c>, which builds
/// <c>Entry(place, tag, checksum, size, time)</c>.
/// </remarks>
public sealed class CacheEntry
{
    public CacheEntry(
        CacheKey key,
        PlaceId place,
        int size,
        uint checksum,
        byte tag,
        DateTimeOffset storeTime,
        DateTimeOffset estimatedLastUse,
        int version)
    {
        Key = key;
        Place = place;
        Size = size;
        Checksum = checksum;
        Tag = tag;
        StoreTime = storeTime;
        EstimatedLastUse = estimatedLastUse;
        Version = version;
    }

    /// <summary>The key this value is stored under.</summary>
    public CacheKey Key { get; }

    /// <summary>Where the value's bytes live on disk.</summary>
    public PlaceId Place { get; }

    /// <summary>Logical value length in bytes (the file is padded to a 16-byte multiple).</summary>
    public int Size { get; }

    /// <summary><c>XXH32(value, 0)</c> of the logical value.</summary>
    public uint Checksum { get; }

    /// <summary>
    /// Opaque category byte written by the client. Observed values in
    /// Telegram Desktop 6&ndash;10 / 64Gram: 1 = still image, 2 = animation
    /// (GIF / Lottie / WebM), 5 = video, 0 = unclassified.
    /// </summary>
    public byte Tag { get; }

    /// <summary>Wall-clock time of the <c>Store</c> record that produced this entry.</summary>
    public DateTimeOffset StoreTime { get; }

    /// <summary>
    /// Best-effort wall-clock estimate of the last access, derived from the
    /// monotonic counter the binlog keeps in <c>StoreWithTime::time</c>.
    /// </summary>
    public DateTimeOffset EstimatedLastUse { get; }

    /// <summary>Cache version folder the entry was read from.</summary>
    public int Version { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Key} place={Place.ToRelativePath()} size={Size} checksum={Checksum:X8}";
}
