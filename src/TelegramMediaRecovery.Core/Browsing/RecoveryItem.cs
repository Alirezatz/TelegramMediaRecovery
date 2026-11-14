using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Core.Browsing;

/// <summary>A verified cache candidate. It holds metadata and source references, never decrypted media buffers.</summary>
public sealed class RecoveryItem
{
    internal RecoveryItem(Guid session, CacheDatabase database, IReadOnlyList<string> versions,
        IReadOnlyList<CacheEntry> entries, MediaFormat format, long size, long cachedBytes,
        bool hasGaps, bool hasHeader, int sequence)
    {
        Session = session;
        Database = database;
        Versions = versions;
        Entries = entries;
        Format = format;
        Usage = CacheUsageClassifier.Classify(entries[0].Key, entries[0].Tag, database.IsMediaCache, format);
        Size = size;
        CachedBytes = cachedBytes;
        HasGaps = hasGaps;
        HasHeader = hasHeader;
        Name = $"{sequence:D6}.{format.Extension}";
        Id = $"{Source}/{entries[0].Key}";
        Date = entries.Max(e => e.EstimatedLastUse);
    }

    internal Guid Session { get; }
    internal CacheDatabase Database { get; }
    internal IReadOnlyList<string> Versions { get; }
    internal IReadOnlyList<CacheEntry> Entries { get; }

    /// <summary>Generated name; the cache does not provide the original filename.</summary>
    public string Name { get; }
    public string Account => Database.Account;
    public string DatabaseName => Database.Name;
    public string Source => Database.ToString();
    public string Id { get; }
    public MediaFormat Format { get; }
    /// <summary>Role inferred from recognized cache key namespaces and sticker tags, not from image appearance.</summary>
    public CacheUsage Usage { get; }
    /// <summary>Known output extent. A stream's true original length may be larger.</summary>
    public long Size { get; }
    public long CachedBytes { get; }
    public bool IsStream => Database.IsMediaCache;
    public bool HasGaps { get; }
    public bool HasHeader { get; }
    public int Fragments => Entries.Count;
    /// <summary>Estimated cache access time, not message time.</summary>
    public DateTimeOffset Date { get; }
}

/// <summary>Read-only discovery results. Scanning does not create an output or preview directory.</summary>
public sealed record RecoveryCatalog(IReadOnlyList<RecoveryItem> Items, int Databases,
    int UnreadableValues, int UnknownValues, IReadOnlyList<string> Warnings);

/// <summary>Progress for scanning and selected export.</summary>
public sealed record RecoveryProgress(string Phase, int Completed, int Total, int Files);

/// <summary>Selected export results, including any completed files when cancellation was requested.</summary>
public sealed record RecoveryExport(Extraction.ExtractionReport Report, IReadOnlyList<string> Failures, bool Cancelled);

/// <summary>A temporary local media file. Close a media player before disposing the lease.</summary>
public sealed class RecoveryPreview : IDisposable
{
    private readonly Action _release;
    internal RecoveryPreview(string path, Action release) => (Path, _release) = (path, release);
    public string Path { get; }
    public void Dispose() => _release();
}
