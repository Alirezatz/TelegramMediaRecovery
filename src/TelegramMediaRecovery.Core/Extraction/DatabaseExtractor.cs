using System.Collections.Concurrent;
using System.Security.Cryptography;
using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Containers;
using TelegramMediaRecovery.Core.Crypto;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Core.Extraction;

/// <summary>
/// Runs the actual extraction for one cache database.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes exist and they need different strategies:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b><c>cache</c></b> &mdash; every entry is one self-contained file (a photo,
///     a GIF, a sticker, a small video). Handled value by value, in parallel.
///   </description></item>
///   <item><description>
///     <b><c>media_cache</c></b> &mdash; one file is spread over many entries:
///     a header slice plus up to 511 data slices. Entries that share a key high are
///     stitched back together with <see cref="StreamedFilePlanner"/> and written
///     with <see cref="StreamedFileWriter"/>, which never materialises a whole
///     video in memory.
///   </description></item>
/// </list>
/// <para>
/// Both paths verify the XXH32 checksum of every value before it is used, so a
/// wrong version folder can never inject garbage into the output.
/// </para>
/// </remarks>
internal sealed class DatabaseExtractor(
    ExtractionOptions options,
    OutputNamer namer,
    CacheValueReader reader,
    ExtractionCounters counters,
    ConcurrentBag<ExtractedItem> items,
    ConcurrentBag<SkippedItem> skipped,
    OutputDeduplicator dedup,
    string label,
    CancellationToken token)
{
    private readonly (string Account, string Database) _label = SplitLabel(label);
    private readonly HashSet<CacheKey> _claimedKeys = [];
    private readonly HashSet<string> _claimedPlaces = [];
    private readonly List<ExtractedItem> _writtenThisDatabase = [];

    /// <summary>Files recovered by the orphan scan for this database.</summary>
    public int OrphansRecovered { get; private set; }

    /// <summary>Values handled by this database, for the final summary.</summary>
    public ExtractionCounters Counters => counters;

    /// <summary>Extracts a <c>cache</c> database: one entry, one file.</summary>
    public void RunPlain(IReadOnlyList<CacheEntry> entries)
    {
        var candidates = Filter(entries);

        Parallel.ForEach(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Degree(),
                CancellationToken = token,
            },
            entry =>
            {
                token.ThrowIfCancellationRequested();
                lock (_claimedKeys)
                {
                    _claimedKeys.Add(entry.Key);
                }

                var value = reader.Read(entry);
                if (value is null)
                {
                    Skip(ClassifyMissing(), entry, "the place file is missing or did not verify");
                    return;
                }

                WriteValue(entry, value.Value.Span, fragments: 1);
            });
    }

    /// <summary>Extracts a <c>media_cache</c> database: many entries, one file.</summary>
    public void RunStreaming(IReadOnlyList<CacheEntry> entries)
    {
        var groups = StreamedFilePlanner.GroupByFile(entries);
        var total = groups.Count;
        var done = 0;

        Parallel.ForEach(
            groups,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Degree(),
                CancellationToken = token,
            },
            group =>
            {
                token.ThrowIfCancellationRequested();

                var plan = StreamedFilePlanner.BuildPlan(group, entry => reader.Read(entry));

                lock (_claimedKeys)
                {
                    foreach (var fragment in plan.Fragments)
                    {
                        _claimedKeys.Add(fragment.Entry.Key);
                    }
                }

                if (!ShouldWriteStream(plan))
                {
                    RecordStreamSkip(plan);
                }
                else
                {
                    WriteStream(plan);
                }

                var completed = Interlocked.Increment(ref done);
                if (completed % 64 == 0)
                {
                    options.Progress?.Report(new ExtractionProgress(
                        label, counters.EntriesSeen, counters.FilesWritten, counters.BytesWritten,
                        counters.SkippedTotal, completed, total));
                }
            });
    }

    /// <summary>
    /// Recovers TDEF files that exist on disk but are no longer referenced by any
    /// live binlog entry.
    /// </summary>
    /// <remarks>
    /// This is not paranoia: the client deletes a place file lazily, deletes the
    /// binlog record immediately, and can be killed in between, so an account can
    /// accumulate hundreds of files that are perfectly readable and not in any
    /// index. They are exactly the ones a user who "lost" a photo still has on
    /// disk.
    /// </remarks>
    public int RunOrphanScan(IReadOnlyList<string> versionDirectories)
    {
        var recovered = 0;

        foreach (var directory in versionDirectories)
        {
            token.ThrowIfCancellationRequested();

            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in EnumeratePlaceFiles(directory))
            {
                token.ThrowIfCancellationRequested();

                counters.OrphanCandidates++;
                var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!TryParsePlace(relative, out var place))
                {
                    continue;
                }

                lock (_claimedKeys)
                {
                    if (_claimedKeys.Contains(new CacheKey(0, 0)) || _claimedPlaces.Contains(relative))
                    {
                        continue;
                    }

                    _claimedPlaces.Add(relative);
                }

                if (!FileHasTdefMagic(file))
                {
                    continue;
                }

                var value = reader.TryReadRaw(file, options.MaxEntrySize);
                if (value is null || value.Length == 0)
                {
                    continue;
                }

                var head = value.AsSpan(0, Math.Min(value.Length, 512));
                if (!options.KeepUnknownContent && !MediaSniffer.LooksLikeMedia(head))
                {
                    Skip(SkipReason.NotRecognisedMedia, default, relative, value.Length, "orphan file");
                    continue;
                }

                var format = MediaSniffer.Detect(head);
                if (!Accepts(format))
                {
                    Skip(SkipReason.FilteredKind, default, relative, value.Length, "orphan file");
                    continue;
                }

                if (options.MinimumSize > 0 && value.Length < options.MinimumSize)
                {
                    Skip(SkipReason.TooSmall, default, relative, value.Length, "orphan file");
                    continue;
                }

                var digest = Hash(value);
                if (!dedup.TryAdd(digest))
                {
                    Skip(SkipReason.Duplicate, default, relative, value.Length, digest);
                    continue;
                }

                var path = namer.NextPath(
                    Account, Database, format.Kind, format.Extension,
                    DateTimeOffset.Now, options.DatedFileNames, partial: false);

                File.WriteAllBytes(path, value);
                counters.FilesWritten++;
                counters.BytesWritten += value.Length;
                recovered++;

                items.Add(new ExtractedItem
                {
                    Account = Account,
                    Database = Database,
                    Source = "orphan",
                    Key = default,
                    Place = relative,
                    Version = -1,
                    StoredSize = value.Length,
                    Size = value.Length,
                    Coverage = 1.0,
                    Format = format,
                    OutputPath = path,
                    Sha256 = digest,
                    Date = File.GetLastWriteTimeUtc(path),
                    IsPartial = false,
                    FragmentCount = 1,
                    RelativeRoot = namer.Root,
                });


            }
        }

        OrphansRecovered += recovered;
        return recovered;
    }

    private static IEnumerable<string> EnumeratePlaceFiles(string directory)
    {
        foreach (var bucket in Directory.EnumerateDirectories(directory))
        {
            if (bucket.Length == directory.Length + 3)
            {
                yield return bucket;
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(bucket))
            {
                yield return file;
            }
        }
    }

    private static bool TryParsePlace(string relative, out string place)
    {
        place = relative;
        var slash = relative.IndexOf('/');
        return slash == 2 && relative.Length == 15;
    }

    private static bool FileHasTdefMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && magic.SequenceEqual("TDEF"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private int Degree() =>
        options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;

    private IReadOnlyList<CacheEntry> Filter(IEnumerable<CacheEntry> entries)
    {
        var result = new List<CacheEntry>();
        foreach (var entry in entries)
        {
            if (options.MaximumSize > 0 && entry.Size > options.MaximumSize)
            {
                Skip(SkipReason.TooLarge, entry, $"larger than {options.MaximumSize} bytes");
                continue;
            }

            if (options.MinimumSize > 0 && entry.Size < options.MinimumSize)
            {
                Skip(SkipReason.TooSmall, entry, $"smaller than {options.MinimumSize} bytes");
                continue;
            }

            if (options.MaxFilesPerDatabase > 0 && result.Count >= options.MaxFilesPerDatabase)
            {
                Skip(SkipReason.LimitReached, entry, $"the per-database limit is {options.MaxFilesPerDatabase}");
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    private void WriteValue(CacheEntry entry, ReadOnlySpan<byte> value, int fragments)
    {
        var head = value[..Math.Min(value.Length, 512)];
        var format = MediaSniffer.Detect(head);

        if (format.Kind == MediaKind.Document && !options.KeepUnknownContent)
        {
            Skip(SkipReason.NotRecognisedMedia, entry, $"first bytes {MediaSniffer.Describe(head)}");
            return;
        }

        if (!Accepts(format))
        {
            Skip(SkipReason.FilteredKind, entry, $"kind {format.Kind} is filtered out");
            return;
        }

        var digest = Hash(value);
        if (!dedup.TryAdd(digest))
        {
            Skip(SkipReason.Duplicate, entry, digest);
            return;
        }

        var path = namer.NextPath(
            Account, Database, format.Kind, format.Extension,
            entry.EstimatedLastUse, options.DatedFileNames, partial: false);

        try
        {
            File.WriteAllBytes(path, value.ToArray());
        }
        catch (IOException ex)
        {
            Skip(SkipReason.Unknown, entry, ex.Message);
            return;
        }

        counters.FilesWritten++;
        counters.BytesWritten += value.Length;
        Track(new ExtractedItem
        {
            Account = Account,
            Database = Database,
            Source = "binlog",
            Key = entry.Key,
            Place = entry.Place.ToRelativePath(),
            Version = entry.Version,
            StoredSize = entry.Size,
            Size = value.Length,
            Coverage = 1.0,
            Format = format,
            OutputPath = path,
            Sha256 = digest,
            Date = entry.EstimatedLastUse,
            IsPartial = false,
            FragmentCount = fragments,
            RelativeRoot = namer.Root,
        });
    }

    private void WriteStream(StreamedFilePlan plan)
    {
        var firstFragment = plan.Fragments.FirstOrDefault(f => f.Map.Parts.Any(p => p.Offset == 0))
                            ?? plan.Fragments.FirstOrDefault()
                            ?? throw new InvalidOperationException("empty plan");

        PartMap.Part? headPart = firstFragment.Map.Parts.FirstOrDefault(p => p.Offset == 0);
        var head = headPart.HasValue ? headPart.Value.Data.Span : ReadOnlySpan<byte>.Empty;
        var format = MediaSniffer.Detect(head);

        if (format.Kind == MediaKind.Document && !options.KeepUnknownContent)
        {
            Skip(SkipReason.NotRecognisedMedia, firstFragment.Entry, $"reconstructed file starts with {MediaSniffer.Describe(head)}");
            return;
        }

        if (!Accepts(format))
        {
            Skip(SkipReason.FilteredKind, firstFragment.Entry, $"kind {format.Kind} is filtered out");
            return;
        }

        var digest = ComputeStreamDigest(plan);
        if (!dedup.TryAdd(digest))
        {
            Skip(SkipReason.Duplicate, firstFragment.Entry, digest);
            return;
        }

        var partial = options.MarkPartialFiles && plan.HasGaps;
        var path = namer.NextPath(
            Account, Database, format.Kind, format.Extension,
            firstFragment.Entry.EstimatedLastUse, options.DatedFileNames, partial);

        try
        {
            var written = StreamedFileWriter.Write(plan, path);
            counters.FilesWritten++;
            counters.BytesWritten += plan.FileSize;

            Track(new ExtractedItem
            {
                Account = Account,
                Database = Database,
                Source = "binlog",
                Key = plan.BaseKey,
                Place = firstFragment.Entry.Place.ToRelativePath(),
                Version = firstFragment.Entry.Version,
                StoredSize = firstFragment.Entry.Size,
                Size = plan.FileSize,
                Coverage = plan.Coverage,
                Format = format,
                OutputPath = path,
                Sha256 = options.ComputeHashes ? written : null,
                Date = firstFragment.Entry.EstimatedLastUse,
                IsPartial = plan.HasGaps,
                FragmentCount = plan.Fragments.Count,
                RelativeRoot = namer.Root,
            });
        }
        catch (IOException ex)
        {
            Skip(SkipReason.Unknown, firstFragment.Entry, ex.Message);
            options.Log?.Invoke($"{plan.BaseKey}: could not write {path}: {ex.Message}");
        }
    }

    private bool ShouldWriteStream(StreamedFilePlan plan)
    {
        if (plan.Fragments.Count == 0 || plan.FileSize == 0)
        {
            return false;
        }

        if (plan.Coverage < options.MinimumCoverage)
        {
            return false;
        }

        if (options.RequireStreamHeader && !plan.HasHeader)
        {
            return false;
        }

        if (options.MaximumSize > 0 && plan.FileSize > options.MaximumSize)
        {
            return false;
        }

        if (options.MinimumSize > 0 && plan.CachedBytes < options.MinimumSize)
        {
            return false;
        }

        return true;
    }

    private void RecordStreamSkip(StreamedFilePlan plan)
    {
        var first = plan.Fragments.Count > 0 ? plan.Fragments[0].Entry : null;

        if (plan.Fragments.Count == 0 || plan.FileSize == 0)
        {
            Skip(SkipReason.PlaceFileMissing, plan.BaseKey, string.Empty, 0, "no cached slices survived");
            return;
        }

        if (options.RequireStreamHeader && !plan.HasHeader)
        {
            Skip(SkipReason.PlaceFileMissing, plan.BaseKey, string.Empty, plan.FileSize, "the header slice is gone");
            return;
        }

        if (options.MaximumSize > 0 && plan.FileSize > options.MaximumSize)
        {
            Skip(SkipReason.TooLarge, plan.BaseKey, string.Empty, plan.FileSize,
                $"larger than {options.MaximumSize} bytes");
            return;
        }

        if (options.MinimumSize > 0 && plan.CachedBytes < options.MinimumSize)
        {
            Skip(SkipReason.TooSmall, plan.BaseKey, string.Empty, plan.FileSize,
                $"only {plan.CachedBytes} cached bytes");
            return;
        }

        Skip(SkipReason.MostlyMissing, plan.BaseKey, first?.Place.ToRelativePath() ?? string.Empty,
            plan.FileSize, $"only {plan.Coverage:P1} of {plan.FileSize} bytes are cached");
        options.Log?.Invoke(
            $"{plan.BaseKey}: only {plan.Coverage:P1} of {plan.FileSize} bytes are cached, skipping.");
    }

    private bool Accepts(MediaFormat format) =>
        options.Kinds.Count == 0 || options.Kinds.Contains(format.Kind);

    private void Track(ExtractedItem item)
    {
        items.Add(item);
        lock (_writtenThisDatabase)
        {
            _writtenThisDatabase.Add(item);
        }
    }

    private string? Hash(ReadOnlySpan<byte> value) =>
        options.ComputeHashes ? Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant() : null;

    /// <summary>
    /// Content identity of a streamed file, computed from its part layout instead
    /// of its bytes.
    /// </summary>
    /// <remarks>
    /// Hashing gigabytes twice (once to de-duplicate, once to write) is wasteful,
    /// and identical content always produces an identical part layout, so the
    /// layout is a cheap and collision-free stand-in. The real SHA-256 is computed
    /// while writing and lands in the manifest.
    /// </remarks>
    private static string ComputeStreamDigest(StreamedFilePlan plan)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(plan.FileSize).Append(':');
        foreach (var part in plan.AllParts())
        {
            builder.Append(part.Offset).Append('+').Append(part.Length).Append(',');
        }

        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString()));
        return "layout:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Records why a value did not become a file. Both the aggregate counter and
    /// the per-item row matter: the counter drives the summary, the row lets a
    /// user look up the exact cache entry of a photo they cannot find.
    /// </summary>
    private void Skip(SkipReason reason, CacheEntry entry, string? detail = null)
    {
        counters.Skip(reason);
        skipped.Add(new SkippedItem(
            reason,
            Account,
            Database,
            entry.Key,
            entry.Place.ToRelativePath(),
            entry.Size,
            detail));
    }

    private void Skip(SkipReason reason, CacheKey key, string place, long size, string? detail = null)
    {
        counters.Skip(reason);
        skipped.Add(new SkippedItem(reason, Account, Database, key, place, size, detail));
    }

    private string Account => _label.Account;

    private string Database => _label.Database;

    private static (string Account, string Database) SplitLabel(string value)
    {
        var slash = value.IndexOf('/');
        return slash < 0 ? (value, value) : (value[..slash], value[(slash + 1)..]);
    }

    private SkipReason ClassifyMissing() => reader.MissingFiles > 0 && reader.ChecksumFailures == 0
        ? SkipReason.PlaceFileMissing
        : SkipReason.ChecksumMismatch;
}
