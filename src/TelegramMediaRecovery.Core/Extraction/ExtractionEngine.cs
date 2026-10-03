using System.Collections.Concurrent;
using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Containers;
using TelegramMediaRecovery.Core.Crypto;
using TelegramMediaRecovery.Core.Media;
using TelegramMediaRecovery.Core.Reporting;

namespace TelegramMediaRecovery.Core.Extraction;

/// <summary>
/// Everything a completed run produced: the recovered files, the skips and the
/// per-database statistics.
/// </summary>
public sealed class ExtractionReport
{
    /// <summary>The recovered files.</summary>
    public required IReadOnlyList<ExtractedItem> Items { get; init; }

    /// <summary>The values that were considered but not written.</summary>
    public required IReadOnlyList<SkippedItem> Skipped { get; init; }

    /// <summary>Aggregate counters.</summary>
    public required ExtractionCounters Counters { get; init; }

    /// <summary>Per-database binlog statistics, in discovery order.</summary>
    public required IReadOnlyList<DatabaseReport> Databases { get; init; }

    /// <summary>Output folder that received the files.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Wall-clock duration of the run.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Files written, by category.</summary>
    public IReadOnlyDictionary<string, int> ByCategory =>
        Items.GroupBy(static i => i.Format.Kind.ToString())
            .ToDictionary(static g => g.Key, static g => g.Count());

    /// <summary>Bytes written, by category.</summary>
    public IReadOnlyDictionary<string, long> BytesByCategory =>
        Items.GroupBy(static i => i.Format.Kind.ToString())
            .ToDictionary(static g => g.Key, static g => g.Sum(static i => i.Size));
}

/// <summary>Statistics for one cache database.</summary>
/// <param name="Account">Account folder.</param>
/// <param name="Database">Database folder name.</param>
/// <param name="Version">Version folder the live binlog came from, or -1.</param>
/// <param name="TrackEstimatedTime">Whether the binlog carried timestamps.</param>
/// <param name="RecordsRead">Records framed by the binlog parser.</param>
/// <param name="LiveEntries">Keys that survived to the end of the live binlog.</param>
/// <param name="OlderVersionEntries">Entries contributed only by stale version folders.</param>
/// <param name="OrphanFilesRecovered">Place files recovered that no entry referenced.</param>
/// <param name="Warnings">Human-readable notes collected while parsing.</param>
public readonly record struct DatabaseReport(
    string Account,
    string Database,
    int Version,
    bool TrackEstimatedTime,
    int RecordsRead,
    int LiveEntries,
    int OlderVersionEntries,
    int OrphanFilesRecovered,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Recovers cached media from a <c>tdata</c> folder.
/// </summary>
/// <remarks>
/// The pipeline is split so that the sequential part stays cheap and only the I/O
/// is parallelised:
/// <list type="number">
///   <item><description>discover the account and cache-database folders;</description></item>
///   <item><description>
///     parse every binlog (one big, sequentially read file each) into live entries;
///   </description></item>
///   <item><description>decrypt and write values on a thread pool.</description></item>
/// </list>
/// Reading older version folders as a fallback is cheap and occasionally rescues
/// files whose place file was written just before a compaction.
/// </remarks>
public sealed class ExtractionEngine(ExtractionOptions options)
{
    private readonly ExtractionOptions _options = options;
    private readonly OutputDeduplicator _dedup = new();

    /// <summary>De-duplicator shared by every database of a run.</summary>
    public OutputDeduplicator Deduplicator => _dedup;

    /// <summary>Runs the whole pipeline for one <c>tdata</c> folder.</summary>
    public ExtractionReport Run(string tdataPath, LocalKey localKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tdataPath);
        ArgumentNullException.ThrowIfNull(localKey);

        var started = DateTimeOffset.Now;
        Directory.CreateDirectory(_options.OutputDirectory);

        var namer = new OutputNamer(_options.OutputDirectory);
        var counters = new ExtractionCounters();
        var databases = CacheDatabaseLocator.Discover(tdataPath).Where(SelectsSource).ToList();

        if (databases.Count == 0)
        {
            throw new InvalidOperationException(
                $"No cache databases (tdata/user_data*/cache|media_cache) were found under {tdataPath}.");
        }

        var items = new ConcurrentBag<ExtractedItem>();
        var skipped = new ConcurrentBag<SkippedItem>();
        var reports = new List<DatabaseReport>(databases.Count);

        for (var index = 0; index < databases.Count; index++)
        {
            _options.CancellationToken.ThrowIfCancellationRequested();

            var database = databases[index];
            var label = database.ToString();
            _options.Trace($"Reading {label} ...");

            var scan = ReadBinlogs(database, localKey);
            counters.EntriesSeen += scan.Entries.Count;

            var reader = new CacheValueReader(localKey, scan.VersionDirectories, _options.Log);
            var extractor = new DatabaseExtractor(
                _options, namer, reader, counters, items, skipped, _dedup, label,
                _options.CancellationToken);

            if (database.IsMediaCache)
            {
                extractor.RunStreaming(scan.Entries);
            }
            else
            {
                extractor.RunPlain(scan.Entries);
            }

            if (_options.ScanOrphans)
            {
                extractor.RunOrphanScan(scan.VersionDirectories);
            }

            counters.Duplicates = _dedup.Duplicates;

            reports.Add(new DatabaseReport(
                database.Account,
                database.Name,
                scan.LiveVersion,
                scan.TrackEstimatedTime,
                scan.RecordsRead,
                scan.LiveEntryCount,
                scan.Entries.Count - scan.LiveEntryCount,
                extractor.OrphansRecovered,
                scan.Warnings));

            _options.Progress?.Report(new ExtractionProgress(
                label,
                counters.EntriesSeen,
                counters.FilesWritten,
                counters.BytesWritten,
                counters.SkippedTotal,
                index + 1,
                databases.Count));
        }

        counters.Duplicates = _dedup.Duplicates;

        var report = new ExtractionReport
        {
            Items = [.. items.OrderByDescending(static i => i.Date)],
            Skipped = [.. skipped],
            Counters = counters,
            Databases = reports,
            OutputDirectory = Path.GetFullPath(_options.OutputDirectory),
            Duration = DateTimeOffset.Now - started,
        };

        WriteReports(report);
        return report;
    }

    /// <summary>Writes the manifest and, if asked, the HTML gallery.</summary>
    private void WriteReports(ExtractionReport report)
    {
        Directory.CreateDirectory(report.OutputDirectory);

        if (_options.WriteManifest)
        {
            CsvManifestWriter.WriteCsv(Path.Combine(report.OutputDirectory, "manifest.csv"), report);
            CsvManifestWriter.WriteSkippedCsv(Path.Combine(report.OutputDirectory, "skipped.csv"), report);
        }

        if (_options.WriteJsonManifest)
        {
            JsonManifestWriter.WriteJson(Path.Combine(report.OutputDirectory, "manifest.json"), report);
        }

        if (_options.WriteHtmlReport)
        {
            HtmlReportWriter.Write(Path.Combine(report.OutputDirectory, "index.html"), report);
        }
    }

    private bool SelectsSource(CacheDatabase database)
    {
        if (_options.Accounts.Count > 0
            && !_options.Accounts.Any(a => string.Equals(a, database.Account, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return _options.Databases.Count == 0
               || _options.Databases.Any(d => string.Equals(d, database.Name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record BinlogScan(
        List<CacheEntry> Entries,
        IReadOnlyList<string> VersionDirectories,
        int LiveVersion,
        bool TrackEstimatedTime,
        int RecordsRead,
        int LiveEntryCount,
        IReadOnlyList<string> Warnings);

    private BinlogScan ReadBinlogs(CacheDatabase database, LocalKey localKey)
    {
        var warnings = new List<string>();
        var versions = database.Versions();

        if (versions.Count == 0)
        {
            warnings.Add("no binlog in any version folder");
            return new BinlogScan([], [], -1, false, 0, 0, warnings);
        }

        var merged = new Dictionary<CacheKey, CacheEntry>();
        var recordsRead = 0;
        var liveEntryCount = 0;
        var trackTime = false;

        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i];

            BinlogParseResult parsed;
            try
            {
                using var file = TdefFile.Open(version.BinlogPath, localKey);
                parsed = CacheBinlogReader.Parse(file.ReadAll(), version.Number, _options.MaxEntrySize);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                           or WrongKeyException or System.Security.Cryptography.CryptographicException)
            {
                warnings.Add($"version {version.Number}: {ex.Message}");
                continue;
            }

            recordsRead += parsed.RecordsRead;
            if (i == 0)
            {
                trackTime = parsed.Header.TrackEstimatedTime;
                liveEntryCount = parsed.Entries.Count;
            }

            if (parsed.RejectedRecords > 0 || parsed.Truncated)
            {
                warnings.Add(
                    $"version {version.Number}: {parsed.Entries.Count} live entries"
                    + (parsed.RejectedRecords > 0 ? $", {parsed.RejectedRecords} record(s) rejected" : string.Empty)
                    + (parsed.Truncated
                        ? $", parsing stopped at byte {parsed.StoppedAtOffset} of {parsed.PayloadLength}"
                        : string.Empty));
            }

            // The live version wins; stale folders only fill in what is missing.
            foreach (var (key, entry) in parsed.Entries)
            {
                if (i == 0)
                {
                    merged[key] = entry;
                }
                else
                {
                    merged.TryAdd(key, entry);
                }
            }
        }

        return new BinlogScan(
            [.. merged.Values],
            [.. versions.Select(static v => v.VersionPath)],
            versions[0].Number,
            trackTime,
            recordsRead,
            liveEntryCount,
            warnings);
    }
}
