using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Containers;
using TelegramMediaRecovery.Core.Crypto;
using TelegramMediaRecovery.Core.Extraction;
using TelegramMediaRecovery.Core.Media;
using TelegramMediaRecovery.Core.Reporting;

namespace TelegramMediaRecovery.Core.Browsing;

/// <summary>
/// Owns a read-only cache session: scan metadata, read an on-demand preview, export selected items.
/// Source checksums are checked again when an item is previewed or exported, since a live client can evict it.
/// </summary>
public sealed class RecoverySession : IDisposable
{
    private readonly LocalKey _key;
    private readonly Guid _id = Guid.NewGuid();
    private readonly ConcurrentDictionary<string, byte> _previews = new(StringComparer.Ordinal);
    private readonly HashSet<RecoveryItem> _items = [];
    private string? _previewRoot;
    private bool _disposed;

    public RecoverySession(string source, string passcode = "")
    {
        SourcePath = Path.GetFullPath(source);
        if (!File.Exists(Path.Combine(SourcePath, "key_datas")) && Directory.Exists(Path.Combine(SourcePath, "tdata")))
        {
            SourcePath = Path.Combine(SourcePath, "tdata");
        }
        _key = LocalKeyLoader.Load(SourcePath, passcode);
    }

    public string SourcePath { get; }

    private sealed record Candidate(MediaFormat Format, long Size, long Cached, bool Gaps, bool Header);
    private sealed record ScanContext(CacheDatabase Database, IReadOnlyList<string> Directories,
        IReadOnlyList<IReadOnlyList<CacheEntry>> Groups);

    public RecoveryCatalog Scan(IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _items.Clear();
        var found = new List<RecoveryItem>();
        var warnings = new List<string>();
        var unreadable = 0;
        var unknown = 0;
        var databases = CacheDatabaseLocator.Discover(SourcePath);
        if (databases.Count == 0) { throw new InvalidDataException("No cache databases were found in this data source."); }
        var contexts = new List<ScanContext>(databases.Count);
        foreach (var database in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = CacheIndexReader.Read(database, _key, cancellationToken: cancellationToken);
            warnings.AddRange(snapshot.Warnings.Select(w => $"{database}: {w}"));
            var groups = database.IsMediaCache
                ? StreamedFilePlanner.GroupByFile(snapshot.Entries).Select(g => (IReadOnlyList<CacheEntry>)g.ToArray()).ToList()
                : snapshot.Entries.OrderBy(e => e.Key).Select(e => (IReadOnlyList<CacheEntry>)new[] { e }).ToList();
            contexts.Add(new ScanContext(database, snapshot.VersionDirectories, groups));
        }
        var total = contexts.Sum(context => context.Groups.Count);
        var processed = 0;
        foreach (var context in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (database, directories, groups) = context;
            var reader = new CacheValueReader(_key, directories);
            var candidates = new Candidate?[groups.Count];
            var completed = 0;
            var identified = 0;
            Parallel.For(0, groups.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, index =>
            {
                try
                {
                    var group = groups[index];
                    Candidate candidate;
                    if (database.IsMediaCache)
                    {
                        var plan = BuildPlan(group, reader, cancellationToken);
                        if (plan.Fragments.Count == 0) { Interlocked.Increment(ref unreadable); return; }
                        candidate = new Candidate(DetectPlan(plan), plan.FileSize, plan.CachedBytes, plan.HasGaps, plan.HasHeader);
                    }
                    else
                    {
                        using var value = reader.Open(group[0], cancellationToken);
                        if (value is null) { Interlocked.Increment(ref unreadable); return; }
                        candidate = new Candidate(MediaSniffer.Detect(value.Memory.Span), value.Memory.Length, value.Memory.Length, false, true);
                    }
                    if (candidate.Format == MediaFormat.Binary) { Interlocked.Increment(ref unknown); }
                    else { candidates[index] = candidate; Interlocked.Increment(ref identified); }
                }
                finally
                {
                    var count = Interlocked.Increment(ref completed);
                    if (count % 32 == 0 || count == groups.Count)
                    {
                        progress?.Report(new RecoveryProgress(database.ToString(), processed + count, total, found.Count + Volatile.Read(ref identified)));
                    }
                }
            });
            for (var index = 0; index < candidates.Length; index++)
            {
                if (candidates[index] is not { } candidate) { continue; }
                var item = new RecoveryItem(_id, database, directories, groups[index], candidate.Format, candidate.Size,
                    candidate.Cached, candidate.Gaps, candidate.Header, found.Count + 1);
                found.Add(item);
                _items.Add(item);
            }
            processed += groups.Count;
        }
        return new RecoveryCatalog(found, databases.Count, unreadable, unknown, warnings);
    }
    /// <summary>Reads at most 64 MiB for a bitmap or sticker preview. Nothing is written to disk.</summary>
    public byte[] ReadPreviewBytes(RecoveryItem item, CancellationToken cancellationToken = default)
    {
        Validate(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (item.Size > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("This image is too large for an in-memory preview. Export it to inspect it.");
        }
        var reader = new CacheValueReader(_key, item.Versions);
        if (!item.IsStream)
        {
            using var value = OpenRequired(reader, item.Entries[0], cancellationToken);
            return value.Memory.ToArray();
        }
        var plan = BuildPlan(item.Entries, reader, cancellationToken);
        EnsureReadable(plan);
        if (plan.FileSize > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("This image is too large for an in-memory preview. Export it to inspect it.");
        }
        using var stream = new MemoryStream((int)plan.FileSize);
        StreamedFileWriter.CopyTo(plan, stream, cancellationToken);
        return stream.ToArray();
    }

    /// <summary>Materializes just the selected media into a disposable temporary file for a local player.</summary>
    public RecoveryPreview CreatePreview(RecoveryItem item, CancellationToken cancellationToken = default)
    {
        Validate(item);
        var root = GetPreviewRoot();
        var path = Path.Combine(root, $"{Guid.NewGuid():N}.{item.Format.Extension}");
        try
        {
            WriteItem(item, path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _previews.TryAdd(path, 0);
            return new RecoveryPreview(path, () => ReleasePreview(path));
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Exports exactly the selected identities into a new run folder. Existing files are never overwritten.</summary>
    public RecoveryExport Export(IEnumerable<RecoveryItem> selection, string destination,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var selected = selection.DistinctBy(i => i.Id).ToList();
        if (selected.Count == 0)
        {
            throw new ArgumentException("Select at least one file to export.", nameof(selection));
        }
        foreach (var item in selected)
        {
            Validate(item);
        }
        EnsureOutsideSource(destination);
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.Combine(Path.GetFullPath(destination), $"recovery-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..37]);
        Directory.CreateDirectory(output);
        var started = Stopwatch.StartNew();
        var written = new List<ExtractedItem>();
        var failures = new List<string>();
        var skipped = new List<SkippedItem>();
        var cancelled = false;
        foreach (var item in selected)
        {
            var path = Path.Combine(output, item.Account, item.DatabaseName, OutputNamer.FolderFor(item.Format.Kind), item.Name);
            var staging = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var actual = WriteItem(item, staging, cancellationToken);
                if (actual.HasGaps)
                {
                    path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(item.Name) + $".partial.{item.Format.Extension}");
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(staging, path, overwrite: false);
                written.Add(new ExtractedItem
                {
                    Account = item.Account,
                    Database = item.DatabaseName,
                    Source = "binlog",
                    Key = item.Entries[0].Key,
                    Place = item.Entries[0].Place.ToRelativePath(),
                    Version = item.Entries[0].Version,
                    StoredSize = item.Entries[0].Size,
                    Size = actual.Size,
                    Coverage = actual.Size == 0 ? 0 : (double)actual.CachedBytes / actual.Size,
                    Format = item.Format,
                    CacheUsage = item.Usage,
                    OutputPath = path,
                    Sha256 = actual.Sha256,
                    Date = item.Date,
                    IsPartial = actual.HasGaps,
                    FragmentCount = actual.Fragments,
                    RelativeRoot = output,
                });
            }
            catch (OperationCanceledException)
            {
                TryDelete(staging);
                cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                TryDelete(staging);
                failures.Add($"{item.Name}: {ex.Message}");
                skipped.Add(new SkippedItem(SkipReason.Unknown, item.Account, item.DatabaseName,
                    item.Entries[0].Key, item.Entries[0].Place.ToRelativePath(), item.Size, ex.Message));
            }
            progress?.Report(new RecoveryProgress("export", written.Count + failures.Count, selected.Count, written.Count));
        }
        var counters = new ExtractionCounters
        {
            EntriesSeen = selected.Count,
            FilesWritten = written.Count,
            BytesWritten = written.Sum(i => i.Size),
        };
        var report = new ExtractionReport
        {
            Items = written,
            Skipped = skipped,
            Counters = counters,
            Databases = [],
            OutputDirectory = output,
            Duration = started.Elapsed,
        };
        foreach (var item in skipped) { counters.Skip(item.Reason); }
        RecoveryReportWriter.Write(report);
        return new RecoveryExport(report, failures, cancelled);
    }

    private sealed record WrittenMedia(string Sha256, long Size, long CachedBytes, int Fragments, bool HasGaps);

    private WrittenMedia WriteItem(RecoveryItem item, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reader = new CacheValueReader(_key, item.Versions);
        if (item.IsStream)
        {
            var actual = StreamedFileWriter.Recover(item.Entries, entry => reader.Open(entry, token), path, item.Size, token);
            var plan = actual.Plan;
            return new WrittenMedia(actual.Sha256, plan.FileSize, plan.CachedBytes, plan.Fragments.Count, plan.HasGaps);
        }
        using var value = OpenRequired(reader, item.Entries[0], token);
        var bytes = value.Memory;
        token.ThrowIfCancellationRequested();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes.Span);
        return new WrittenMedia(Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(), bytes.Length, bytes.Length, 1, false);
    }

    private static CacheValueLease OpenRequired(CacheValueReader reader, CacheEntry entry, CancellationToken token) => reader.Open(entry, token)
        ?? throw new InvalidDataException("The cache entry is missing or changed. Scan the data source again.");

    private static StreamedFilePlan BuildPlan(IReadOnlyList<CacheEntry> entries, CacheValueReader reader, CancellationToken token) =>
        StreamedFilePlanner.BuildVerifiedPlan(entries, entry => reader.Open(entry, token), cancellationToken: token);

    private static void EnsureReadable(StreamedFilePlan plan)
    {
        if (plan.Fragments.Count == 0 || plan.FileSize == 0)
        {
            throw new InvalidDataException("The cached fragments are no longer readable. Scan the data source again.");
        }
    }

    private static MediaFormat DetectPlan(StreamedFilePlan plan)
    {
        return MediaSniffer.Detect(plan.Head.Span);
    }

    private string GetPreviewRoot()
    {
        lock (_previews)
        {
            if (_previewRoot is null)
            {
                var root = Path.Combine(Path.GetTempPath(), "TelegramMediaRecovery", "preview-" + _id.ToString("N"));
                EnsureOutsideSource(root);
                Directory.CreateDirectory(root);
                _previewRoot = root;
            }
            return _previewRoot;
        }
    }

    private void EnsureOutsideSource(string destination)
    {
        var source = ResolveDirectory(SourcePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = ResolveDirectory(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.StartsWith(source, comparison))
        {
            throw new ArgumentException("Choose an output folder outside the source tdata directory.", nameof(destination));
        }
    }

    private static string ResolveDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
        }
        return current;
    }

    private void Validate(RecoveryItem item)
    {
        ThrowIfDisposed();
        if (item.Session != _id || !_items.Contains(item))
        {
            throw new ArgumentException("This item belongs to a different or expired scan.", nameof(item));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ReleasePreview(string path)
    {
        if (_previews.TryRemove(path, out _))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        foreach (var path in _previews.Keys) { ReleasePreview(path); }
        if (_previewRoot is not null)
        {
            try { Directory.Delete(_previewRoot); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
