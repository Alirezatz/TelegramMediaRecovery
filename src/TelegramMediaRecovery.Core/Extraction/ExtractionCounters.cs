using System.Collections.Concurrent;

namespace TelegramMediaRecovery.Core.Extraction;

/// <summary>
/// Thread-safe tally of what a run produced.
/// </summary>
public sealed class ExtractionCounters
{
    private readonly ConcurrentDictionary<SkipReason, long> _skipped = new();

    /// <summary>Binlog entries read across all databases.</summary>
    public long EntriesSeen;

    /// <summary>Files written to the output folder.</summary>
    public long FilesWritten;

    /// <summary>Bytes written to the output folder.</summary>
    public long BytesWritten;

    /// <summary>Values skipped, per reason.</summary>
    public IReadOnlyDictionary<SkipReason, long> Skipped => _skipped;

    /// <summary>Total number of skipped values.</summary>
    public long SkippedTotal => _skipped.Values.Sum();

    /// <summary>Counted SHA-256 digests that were already seen.</summary>
    public long Duplicates;

    /// <summary>TDEF files found on disk that no live binlog entry references.</summary>
    public long OrphanCandidates;

    /// <summary>Records a skipped value.</summary>
    public void Skip(SkipReason reason) => _skipped.AddOrUpdate(reason, 1, static (_, v) => v + 1);

    /// <summary>Merges the counters of a finished database into this one.</summary>
    public void Merge(ExtractionCounters other)
    {
        EntriesSeen += other.EntriesSeen;
        FilesWritten += other.FilesWritten;
        BytesWritten += other.BytesWritten;
        Duplicates += other.Duplicates;
        OrphanCandidates += other.OrphanCandidates;

        foreach (var (reason, count) in other._skipped)
        {
            _skipped.AddOrUpdate(reason, count, (_, v) => v + count);
        }
    }
}
