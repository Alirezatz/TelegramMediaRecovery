using TelegramMediaRecovery.Core.Containers;
using TelegramMediaRecovery.Core.Crypto;

namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// Turns the decrypted bytes of a binlog into the set of live cache entries.
/// </summary>
/// <remarks>
/// Playback semantics follow lib_storage
/// <c>storage/cache/storage_cache_database_object.cpp</c> &rarr;
/// <c>readBinlog()</c>:
/// <list type="bullet">
///   <item><description><c>Store</c>/<c>MultiStore</c> insert or replace an entry;</description></item>
///   <item><description><c>MultiRemove</c> erases keys;</description></item>
///   <item><description><c>MultiAccess</c> only refreshes the access clock.</description></item>
/// </list>
/// Reading stops at the first record whose framing does not make sense, exactly
/// like <c>BinlogWrapper::readRecord()</c>, so a half-written tail (the app was
/// killed mid-append) costs us the tail but never the whole file.
/// <para>
/// Timestamps: <c>StoreWithTime::time</c> is an <c>EstimatedTimePoint</c>, i.e. a
/// monotonic counter (<c>relative</c>) plus the wall clock reading at the moment
/// the counter had that value (<c>system</c>). The client keeps a running maximum
/// of <c>relative</c>, so converting any single entry back to wall-clock time is
/// <c>system + (maxRelative - relative)</c>. That estimate is what
/// <see cref="CacheEntry.EstimatedLastUse"/> holds.
/// </para>
/// </remarks>
public static class CacheBinlogReader
{
    private static readonly DateTimeOffset UnixEpoch = DateTimeOffset.FromUnixTimeSeconds(0);

    /// <summary>Parses a decrypted binlog payload.</summary>
    /// <param name="payload">Bytes returned by <c>TdefFile.ReadAll()</c>.</param>
    /// <param name="version">Cache version folder, recorded on every entry.</param>
    /// <param name="maxEntrySize">
    /// Values larger than this are counted as rejected. Defaults to the hard
    /// <c>kDataSizeLimit</c> of 128 MiB; the client itself caps at 16 MiB, but an
    /// install with the cache limits removed may legitimately hold bigger values.
    /// </param>
    public static BinlogParseResult Parse(
        ReadOnlySpan<byte> payload,
        int version,
        int maxEntrySize = CacheRecordLayout.MaxEntrySize)
    {
        if (payload.Length < CacheBasicHeader.Size)
        {
            throw new InvalidDataException(
                $"A binlog payload must be at least {CacheBasicHeader.Size} bytes, got {payload.Length}.");
        }

        var header = CacheBasicHeader.Parse(payload);
        if (header.Format != CacheBasicHeader.SupportedFormat)
        {
            throw new InvalidDataException($"Unsupported cache binlog format {header.Format}.");
        }

        var scanner = new BinlogScanner(
            payload,
            header.TrackEstimatedTime,
            version,
            maxEntrySize);
        scanner.Run();

        return scanner.BuildResult(header);
    }

    /// <summary>Reads a binlog straight off disk, decrypting it with the local key.</summary>
    public static BinlogParseResult ReadFile(
        string path,
        LocalKey key,
        int version,
        int maxEntrySize = CacheRecordLayout.MaxEntrySize)
    {
        using var file = TdefFile.Open(path, key);
        return Parse(file.ReadAll(), version, maxEntrySize);
    }

    private ref struct BinlogScanner
    {
        private readonly ReadOnlySpan<byte> _payload;
        private readonly int _partSize;
        private readonly int _version;
        private readonly int _maxEntrySize;

        private readonly Dictionary<CacheKey, PendingEntry> _entries;

        private ulong _maxRelative;
        private int _offset;
        private int _records;
        private int _stores;
        private int _multiStores;
        private int _multiRemoves;
        private int _multiAccesses;
        private int _rejected;


        public BinlogScanner(
            ReadOnlySpan<byte> payload,
            bool trackEstimatedTime,
            int version,
            int maxEntrySize)
        {
            _payload = payload;
            _partSize = trackEstimatedTime
                ? CacheRecordLayout.StoreWithTime
                : CacheRecordLayout.Store;
            _version = version;
            _maxEntrySize = maxEntrySize;
            _entries = [];
            _offset = CacheBasicHeader.Size;
        }

        public void Run()
        {
            while (_offset + CacheRecordLayout.MultiHead <= _payload.Length)
            {
                switch (CacheRecordLayout.TypeOf(_payload[_offset..]))
                {
                    case CacheRecordType.Store:
                        {
                            if (_offset + _partSize > _payload.Length)
                            {
                                return;
                            }

                            var record = _payload.Slice(_offset, _partSize);
                            if (!IsUsableSize(CacheRecordLayout.EntrySizeOf(record)))
                            {
                                // The client treats an impossible size as a hard stop
                                // (processRecordStoreGeneric returns false, which ends
                                // readTillEnd), so the cursor stays on the bad record
                                // and the scan ends there.
                                _rejected++;
                                return;
                            }

                            ApplyStore(record);
                            _stores++;
                            _records++;
                            _offset += _partSize;
                            break;
                        }

                    case CacheRecordType.MultiStore:
                        {
                            var count = CacheRecordLayout.MultiCountOf(_payload[_offset..]);
                            if (!Fits(count, _partSize))
                            {
                                _rejected++;
                                return;
                            }

                            for (var i = 0; i < count; i++)
                            {
                                ApplyStore(_payload.Slice(_offset + CacheRecordLayout.MultiHead + (i * _partSize), _partSize));
                            }

                            _multiStores++;
                            _records++;
                            _offset += CacheRecordLayout.MultiHead + (count * _partSize);
                            break;
                        }

                    case CacheRecordType.MultiRemove:
                        {
                            var count = CacheRecordLayout.MultiCountOf(_payload[_offset..]);
                            if (!Fits(count, CacheRecordLayout.Key))
                            {
                                _rejected++;
                                return;
                            }

                            for (var i = 0; i < count; i++)
                            {
                                _entries.Remove(ReadPartKey(_offset, i));
                            }

                            _multiRemoves++;
                            _records++;
                            _offset += CacheRecordLayout.MultiHead + (count * CacheRecordLayout.Key);
                            break;
                        }

                    case CacheRecordType.MultiAccess:
                        {
                            var count = CacheRecordLayout.MultiCountOf(_payload[_offset..]);
                            if (!Fits(count, CacheRecordLayout.Key))
                            {
                                return;
                            }

                            var (relative, _) = CacheRecordLayout.MultiAccessTimeOf(_payload[_offset..]);
                            Track(relative);

                            for (var i = 0; i < count; i++)
                            {
                                if (_entries.TryGetValue(ReadPartKey(_offset, i), out var entry))
                                {
                                    entry.LastAccessRelative = relative;
                                }
                            }

                            _multiAccesses++;
                            _records++;
                            _offset += CacheRecordLayout.MultiHead + (count * CacheRecordLayout.Key);
                            break;
                        }

                    default:
                        return;
                }
            }
        }

        public BinlogParseResult BuildResult(CacheBasicHeader header)
        {
            var finished = new Dictionary<CacheKey, CacheEntry>(_entries.Count);
            foreach (var (key, pending) in _entries)
            {
                var storeTime = FromUnix(pending.StoreSystem);
                var lastUse = pending.LastAccessRelative >= pending.StoreRelative
                    ? FromUnix(AddSkew(pending.StoreSystem, _maxRelative - pending.LastAccessRelative))
                    : storeTime;

                finished[key] = new CacheEntry(
                    key, pending.Place, pending.Size, pending.Checksum, pending.Tag,
                    storeTime, lastUse, _version);
            }

            return new BinlogParseResult(
                header,
                finished,
                _payload.Length,
                _records,
                _stores,
                _multiStores,
                _multiRemoves,
                _multiAccesses,
                _rejected,
                Math.Min(_offset, _payload.Length));
        }

        private CacheKey ReadPartKey(int headOffset, int index) => CacheRecordLayout.PartKeyOf(
            _payload.Slice(
                headOffset + CacheRecordLayout.MultiHead + (index * CacheRecordLayout.Key),
                CacheRecordLayout.Key));

        private void ApplyStore(ReadOnlySpan<byte> record)
        {
            var withTime = _partSize == CacheRecordLayout.StoreWithTime;
            var systemTime = withTime ? CacheRecordLayout.SystemTimeOf(record) : 0;
            var relative = withTime ? CacheRecordLayout.RelativeOf(record) : 0;
            Track(relative);

            _entries[CacheRecordLayout.KeyOf(record)] = new PendingEntry
            {
                Place = new PlaceId(CacheRecordLayout.PlaceOf(record)),
                Size = CacheRecordLayout.EntrySizeOf(record),
                Checksum = CacheRecordLayout.ChecksumOf(record),
                Tag = CacheRecordLayout.TagOf(record),
                StoreSystem = systemTime,
                StoreRelative = relative,
                LastAccessRelative = relative,
            };
        }

        private void Track(ulong relative)
        {
            if (relative > _maxRelative)
            {
                _maxRelative = relative;
            }
        }

        private bool IsUsableSize(int size) => size > 0 && size <= _maxEntrySize;

        private bool Fits(int count, int partSize)
        {
            if (count <= 0 || count > CacheRecordLayout.MaxBundledRecords)
            {
                return false;
            }

            var total = (long)CacheRecordLayout.MultiHead + ((long)count * partSize);
            return total <= _payload.Length - _offset;
        }

        private static long AddSkew(uint systemTime, ulong skew) =>
            (long)systemTime + (long)Math.Min(skew, (ulong)int.MaxValue);

        private static DateTimeOffset FromUnix(long seconds) =>
            seconds == 0 ? UnixEpoch : DateTimeOffset.FromUnixTimeSeconds(seconds);

        private sealed class PendingEntry
        {
            public PlaceId Place { get; init; }

            public int Size { get; init; }

            public uint Checksum { get; init; }

            public byte Tag { get; init; }

            public uint StoreSystem { get; init; }

            public ulong StoreRelative { get; init; }

            public ulong LastAccessRelative { get; set; }
        }
    }
}
