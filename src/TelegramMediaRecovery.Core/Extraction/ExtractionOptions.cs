using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Core.Extraction;

/// <summary>
/// Why a value did not end up as an output file. Reported per reason so the user
/// can tell "nothing was cached" apart from "the cache is damaged".
/// </summary>
public enum SkipReason
{
    /// <summary>The place file is gone from disk.</summary>
    PlaceFileMissing,

    /// <summary>The place file exists but does not open with our local key.</summary>
    WrongLocalKey,

    /// <summary>The place file is truncated or not a TDEF container.</summary>
    PlaceFileUnreadable,

    /// <summary>The decrypted value did not match its XXH32 checksum.</summary>
    ChecksumMismatch,

    /// <summary>Content is not recognisable media and unknown content is not kept.</summary>
    NotRecognisedMedia,

    /// <summary>Identical content was already written.</summary>
    Duplicate,

    /// <summary>Smaller than the configured minimum size.</summary>
    TooSmall,

    /// <summary>Larger than the configured maximum size.</summary>
    TooLarge,

    /// <summary>Too little of a streamed file survived eviction to be worth writing.</summary>
    MostlyMissing,

    /// <summary>Excluded by the configured media-kind filter.</summary>
    FilteredKind,

    /// <summary>Excluded by the account/database filter.</summary>
    FilteredSource,

    /// <summary>The limit on files per database was reached.</summary>
    LimitReached,

    /// <summary>Already present in the output folder (resume mode).</summary>
    AlreadyExtracted,

    /// <summary>Anything the tool could not classify.</summary>
    Unknown,
}

/// <summary>
/// User-tunable knobs for one extraction run.
/// </summary>
public sealed class ExtractionOptions
{
    /// <summary>Folder that receives the recovered files.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Only consider these account folders; empty means all.</summary>
    public IReadOnlyCollection<string> Accounts { get; init; } = [];

    /// <summary>Only consider these databases (<c>cache</c>, <c>media_cache</c>); empty means all.</summary>
    public IReadOnlyCollection<string> Databases { get; init; } = [];

    /// <summary>Skip values smaller than this many bytes.</summary>
    public long MinimumSize { get; init; }

    /// <summary>Skip values larger than this many bytes; 0 means no limit.</summary>
    public long MaximumSize { get; init; }

    /// <summary>Keep values that are not recognisable media, as <c>.bin</c>.</summary>
    public bool KeepUnknownContent { get; init; }

    /// <summary>Write content that could not be identified into <c>.bin</c> files as well.</summary>
    public bool IncludeUnknownKind { get; init; } = true;

    /// <summary>Only these media kinds; empty means all.</summary>
    public IReadOnlyCollection<MediaKind> Kinds { get; init; } = [];

    /// <summary>
    /// Minimum share of a streamed file that must be cached for it to be written.
    /// A video whose mdat was evicted comes back as gigabytes of zeros otherwise.
    /// </summary>
    public double MinimumCoverage { get; init; } = 0.05;

    /// <summary>Skip a streamed file completely when it has no header slice at all.</summary>
    public bool RequireStreamHeader { get; init; }

    /// <summary>Hard cap on how many files a single database may contribute; 0 means no cap.</summary>
    public int MaxFilesPerDatabase { get; init; }

    /// <summary>Number of worker threads; 0 means "one per CPU".</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>Also try to recover TDEF files that no live binlog entry references.</summary>
    public bool ScanOrphans { get; init; } = true;

    /// <summary>Skip files whose output already exists (used together with the manifest).</summary>
    public bool Resume { get; init; }

    /// <summary>Write a CSV manifest next to the output.</summary>
    public bool WriteManifest { get; init; } = true;

    /// <summary>Write a JSON-lines manifest next to the output.</summary>
    public bool WriteJsonManifest { get; init; }

    /// <summary>Write a browsable HTML gallery next to the output.</summary>
    public bool WriteHtmlReport { get; init; }

    /// <summary>Compute the SHA-256 of every written file and de-duplicate on it.</summary>
    public bool ComputeHashes { get; init; } = true;

    /// <summary>Prefix every output file name with its estimated date, e.g. <c>20260901-142233_</c>.</summary>
    public bool DatedFileNames { get; init; }

    /// <summary>Add <c>.partial</c> to the extension of incomplete streamed files.</summary>
    public bool MarkPartialFiles { get; init; } = true;

    /// <summary>Also read older cache version folders, not just the current one.</summary>
    public bool IncludeOldVersions { get; init; } = true;

    /// <summary>Values larger than this are rejected outright (<c>kDataSizeLimit</c> by default).</summary>
    public int MaxEntrySize { get; init; } = CacheRecordLayout.MaxEntrySize;

    /// <summary>Sink for human-readable progress messages; may be <c>null</c>.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Reports periodic progress.</summary>
    public IProgress<ExtractionProgress>? Progress { get; init; }

    /// <summary>Cancels a running extraction.</summary>
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;

    internal void Trace(string message) => Log?.Invoke(message);
}
