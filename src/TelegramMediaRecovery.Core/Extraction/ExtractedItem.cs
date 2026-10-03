using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Core.Extraction;

/// <summary>
/// One recovered file, as recorded in the manifest and the HTML report.
/// </summary>
public sealed class ExtractedItem
{
    /// <summary>Account folder the value came from, e.g. <c>user_data#3</c>.</summary>
    public required string Account { get; init; }

    /// <summary>Database folder, <c>cache</c> or <c>media_cache</c>.</summary>
    public required string Database { get; init; }

    /// <summary>How the file was found: <c>binlog</c> or <c>orphan</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Cache key the value was stored under.</summary>
    public required CacheKey Key { get; init; }

    /// <summary>Place id inside the version folder.</summary>
    public required string Place { get; init; }

    /// <summary>Cache version folder the bytes were read from.</summary>
    public int Version { get; init; }

    /// <summary>Logical size of the cached value.</summary>
    public int StoredSize { get; init; }

    /// <summary>Size of the reconstructed file (streamed files may be larger).</summary>
    public long Size { get; init; }

    /// <summary>Share of the file that was actually cached, 0..1.</summary>
    public double Coverage { get; init; } = 1.0;

    /// <summary>Detected container.</summary>
    public required MediaFormat Format { get; init; }

    /// <summary>Absolute path of the written file, or <c>null</c> when nothing was written.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Lowercase hex SHA-256 of the written bytes, when hashing was enabled.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Best-effort date of the cached value.</summary>
    public DateTimeOffset Date { get; init; }

    /// <summary>True when the file has gaps filled with zeros.</summary>
    public bool IsPartial { get; init; }

    /// <summary>Number of cached values that were stitched together.</summary>
    public int FragmentCount { get; init; } = 1;

    /// <summary>Path relative to the output directory, with forward slashes.</summary>
    public string RelativePath =>
        OutputPath is null
            ? string.Empty
            : Path.GetRelativePath(RelativeRoot, OutputPath).Replace('\\', '/');

    /// <summary>Root the relative paths are computed against; set by the engine.</summary>
    public string RelativeRoot { get; init; } = string.Empty;
}

/// <summary>
/// A value that was considered but not written.
/// </summary>
/// <param name="Reason">Why it was skipped.</param>
/// <param name="Account">Account folder it came from.</param>
/// <param name="Database">Database folder it came from.</param>
/// <param name="Key">Cache key.</param>
/// <param name="Place">Place id, for manual inspection.</param>
/// <param name="Size">Logical size in bytes.</param>
/// <param name="Detail">Extra context for the log.</param>
public readonly record struct SkippedItem(
    SkipReason Reason,
    string Account,
    string Database,
    CacheKey Key,
    string Place,
    long Size,
    string? Detail = null);

/// <summary>
/// Periodic progress snapshot handed to <see cref="IProgress{T}"/>.
/// </summary>
/// <param name="CurrentDatabase">Database currently being processed.</param>
/// <param name="EntriesSeen">Binlog entries read so far.</param>
/// <param name="FilesWritten">Files written so far.</param>
/// <param name="BytesWritten">Bytes written so far.</param>
/// <param name="Skipped">Values skipped so far.</param>
/// <param name="DatabasesDone">Databases finished so far.</param>
/// <param name="DatabasesTotal">Databases to process.</param>
public readonly record struct ExtractionProgress(
    string CurrentDatabase,
    long EntriesSeen,
    long FilesWritten,
    long BytesWritten,
    long Skipped,
    int DatabasesDone,
    int DatabasesTotal);
