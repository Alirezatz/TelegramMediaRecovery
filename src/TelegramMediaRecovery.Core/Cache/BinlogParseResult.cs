namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// Outcome of parsing one binlog: the live entries plus enough statistics to tell
/// the user how much of the file we understood.
/// </summary>
public sealed class BinlogParseResult
{
    public BinlogParseResult(
        CacheBasicHeader header,
        Dictionary<CacheKey, CacheEntry> entries,
        int payloadLength,
        int recordsRead,
        int storeRecords,
        int multiStoreRecords,
        int multiRemoveRecords,
        int multiAccessRecords,
        int rejectedRecords,
        int stoppedAtOffset)
    {
        Header = header;
        Entries = entries;
        PayloadLength = payloadLength;
        RecordsRead = recordsRead;
        StoreRecords = storeRecords;
        MultiStoreRecords = multiStoreRecords;
        MultiRemoveRecords = multiRemoveRecords;
        MultiAccessRecords = multiAccessRecords;
        RejectedRecords = rejectedRecords;
        StoppedAtOffset = stoppedAtOffset;
    }

    /// <summary>The header that was read at offset 0.</summary>
    public CacheBasicHeader Header { get; }

    /// <summary>Keys that survive to the end of the binlog.</summary>
    public Dictionary<CacheKey, CacheEntry> Entries { get; }

    /// <summary>Total number of bytes in the decrypted binlog payload.</summary>
    public int PayloadLength { get; }

    /// <summary>Total records that were framed successfully.</summary>
    public int RecordsRead { get; }

    /// <summary>Number of <c>Store</c>/<c>StoreWithTime</c> records.</summary>
    public int StoreRecords { get; }

    /// <summary>Number of <c>MultiStore</c> bundles.</summary>
    public int MultiStoreRecords { get; }

    /// <summary>Number of <c>MultiRemove</c> bundles.</summary>
    public int MultiRemoveRecords { get; }

    /// <summary>Number of <c>MultiAccess</c> bundles.</summary>
    public int MultiAccessRecords { get; }

    /// <summary>Records skipped because their declared size was implausible.</summary>
    public int RejectedRecords { get; }

    /// <summary>Offset the reader stopped at; equals <see cref="PayloadLength"/> when the whole file parsed.</summary>
    public int StoppedAtOffset { get; }

    /// <summary>True when the reader stopped before the end of the payload.</summary>
    public bool Truncated => StoppedAtOffset < PayloadLength;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Entries.Count} live entries from {RecordsRead} records"
        + (RejectedRecords > 0 ? $", {RejectedRecords} rejected" : string.Empty);
}
