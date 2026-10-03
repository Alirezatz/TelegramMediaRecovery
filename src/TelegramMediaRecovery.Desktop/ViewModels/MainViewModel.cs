using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using TelegramMediaRecovery.Core;
using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Crypto;
using TelegramMediaRecovery.Core.Extraction;
using TelegramMediaRecovery.Core.Media;
using TelegramMediaRecovery.Core.Reporting;

namespace TelegramMediaRecovery.Desktop.ViewModels;

/// <summary>
/// Everything the main window binds to: the chosen folders, the options, the
/// live log and the finished summary.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cancellation;

    private string _tdataPath = DefaultTdata();
    private string _outputPath = DefaultOutput();
    private string _passcode = string.Empty;
    private string _accounts = string.Empty;
    private bool _includeCache = true;
    private bool _includeMediaCache = true;
    private bool _keepUnknown;
    private bool _scanOrphans = true;
    private bool _computeHashes = true;
    private bool _datedNames;
    private bool _markPartials = true;
    private bool _htmlReport = true;
    private bool _jsonManifest;
    private bool _verbose;
    private bool _requireHeader;
    private double _minimumCoverage = 0.05;
    private string _minimumSize = string.Empty;
    private string _maximumSize = string.Empty;
    private int _threads;
    private bool _isRunning;
    private double _progress;
    private string _status = "Ready.";
    private string _summary = string.Empty;
    private LocalKey? _localKey;
    private ExtractionReport? _report;

    /// <summary>Creates the view model bound to a UI dispatcher.</summary>
    public MainViewModel(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Raised when the user asks to open a folder; the view handles it.</summary>
    public event EventHandler<string>? OpenFolderRequested;

    // ----------------------------------------------------------------- input

    /// <summary>The <c>tdata</c> folder to read.</summary>
    public string TdataPath
    {
        get => _tdataPath;
        set => Set(ref _tdataPath, value);
    }

    /// <summary>Where recovered files are written.</summary>
    public string OutputPath
    {
        get => _outputPath;
        set => Set(ref _outputPath, value);
    }

    /// <summary>Local passcode; empty when none is set.</summary>
    public string Passcode
    {
        get => _passcode;
        set => Set(ref _passcode, value);
    }

    /// <summary>Comma separated account filter, e.g. <c>user_data,user_data#2</c>.</summary>
    public string Accounts
    {
        get => _accounts;
        set => Set(ref _accounts, value);
    }

    /// <summary>Process the <c>cache</c> database (photos, GIFs, stickers).</summary>
    public bool IncludeCache
    {
        get => _includeCache;
        set => Set(ref _includeCache, value);
    }

    /// <summary>Process the <c>media_cache</c> database (streamed video/audio).</summary>
    public bool IncludeMediaCache
    {
        get => _includeMediaCache;
        set => Set(ref _includeMediaCache, value);
    }

    /// <summary>Also save content that is not recognised as media.</summary>
    public bool KeepUnknown
    {
        get => _keepUnknown;
        set => Set(ref _keepUnknown, value);
    }

    /// <summary>Also recover TDEF files no live binlog entry references.</summary>
    public bool ScanOrphans
    {
        get => _scanOrphans;
        set => Set(ref _scanOrphans, value);
    }

    /// <summary>Hash and de-duplicate the output.</summary>
    public bool ComputeHashes
    {
        get => _computeHashes;
        set => Set(ref _computeHashes, value);
    }

    /// <summary>Prefix file names with their estimated date.</summary>
    public bool DatedNames
    {
        get => _datedNames;
        set => Set(ref _datedNames, value);
    }

    /// <summary>Mark incomplete streamed files with a <c>.partial</c> infix.</summary>
    public bool MarkPartials
    {
        get => _markPartials;
        set => Set(ref _markPartials, value);
    }

    /// <summary>Write an HTML gallery next to the recovered files.</summary>
    public bool WriteHtmlReport
    {
        get => _htmlReport;
        set => Set(ref _htmlReport, value);
    }

    /// <summary>Also write <c>manifest.json</c>.</summary>
    public bool WriteJsonManifest
    {
        get => _jsonManifest;
        set => Set(ref _jsonManifest, value);
    }

    /// <summary>Show per-file log lines.</summary>
    public bool Verbose
    {
        get => _verbose;
        set => Set(ref _verbose, value);
    }

    /// <summary>Only write streamed files whose header slice survived.</summary>
    public bool RequireStreamHeader
    {
        get => _requireHeader;
        set => Set(ref _requireHeader, value);
    }

    /// <summary>Minimum share of a streamed file that must be cached, 0..1.</summary>
    public double MinimumCoverage
    {
        get => _minimumCoverage;
        set => Set(ref _minimumCoverage, value);
    }

    /// <summary>Skip values smaller than this, e.g. <c>4KB</c>.</summary>
    public string MinimumSize
    {
        get => _minimumSize;
        set => Set(ref _minimumSize, value);
    }

    /// <summary>Skip values larger than this.</summary>
    public string MaximumSize
    {
        get => _maximumSize;
        set => Set(ref _maximumSize, value);
    }

    /// <summary>Worker threads; 0 means one per CPU.</summary>
    public int Threads
    {
        get => _threads;
        set => Set(ref _threads, value);
    }

    // ---------------------------------------------------------------- output

    /// <summary>True while an extraction is running.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                Raise(nameof(StartCommand));
                Raise(nameof(CancelCommand));
                Raise(nameof(ScanCommand));
            }
        }
    }

    /// <summary>Progress of the current run, 0..100.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>One-line status shown under the progress bar.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Multi-line summary shown after a run.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>The activity log, newest last.</summary>
    public ObservableCollection<LogEntry> Log { get; } = [];

    /// <summary>Per-database results of the last run.</summary>
    public ObservableCollection<DatabaseSummary> Databases { get; } = [];

    /// <summary>Bytes written so far, formatted.</summary>
    public string BytesWritten => Format(_currentBytes);

    private long _currentBytes;

    // -------------------------------------------------------------- commands

    /// <summary>Browses for the <c>tdata</c> folder.</summary>
    public RelayCommand BrowseTdataCommand => new(_ => BrowseForFolder(TdataPath, path => TdataPath = path));

    /// <summary>Browses for the output folder.</summary>
    public RelayCommand BrowseOutputCommand => new(_ => BrowseForFolder(OutputPath, path => OutputPath = path));

    /// <summary>Reads the binlogs and reports what is inside, without writing files.</summary>
    public RelayCommand ScanCommand => new(_ => _ = ScanAsync(), _ => !IsRunning);

    /// <summary>Starts the extraction.</summary>
    public RelayCommand StartCommand => new(_ => _ = StartAsync(), _ => !IsRunning && TdataPath.Length > 0);

    /// <summary>Stops the extraction.</summary>
    public RelayCommand CancelCommand => new(_ => _cancellation?.Cancel(), _ => IsRunning);

    /// <summary>Opens the output folder in Explorer.</summary>
    public RelayCommand OpenOutputCommand => new(_ => OpenFolderRequested?.Invoke(this, OutputPath));

    /// <summary>Opens the HTML report in the default browser.</summary>
    public RelayCommand OpenReportCommand => new(
        _ => OpenFolderRequested?.Invoke(this, Path.Combine(OutputPath, "index.html")),
        _ => File.Exists(Path.Combine(OutputPath, "index.html")));

    // ----------------------------------------------------------------- logic

    private async Task ScanAsync()
    {
        try
        {
            IsRunning = true;
            Progress = 0;
            Status = "Reading binlogs ...";

            var report = await Task.Run(() => DescribeFolder()).ConfigureAwait(true);

            foreach (var entry in report)
            {
                Log.Add(entry);
            }

            Status = $"{report.Count} database version(s) readable.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or LocalBlobCipher.WrongPasscodeException)
        {
            Append(LogLevel.Error, ex.Message);
            Status = "Could not read the folder.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task StartAsync()
    {
        _cancellation = new CancellationTokenSource();
        Log.Clear();
        Databases.Clear();
        Summary = string.Empty;
        _currentBytes = 0;
        Raise(nameof(BytesWritten));
        Progress = 0;
        IsRunning = true;

        try
        {
            Status = "Deriving the local key ...";

            var options = BuildOptions();
            var report = await Task
                .Run(() =>
                {
                    _localKey ??= LoadLocalKey();
                    return new ExtractionEngine(options).Run(TdataPath, _localKey);
                }, _cancellation.Token)
                .ConfigureAwait(true);

            _report = report;
            Append(LogLevel.Success, $"Finished in {report.Duration.TotalSeconds:0.0}s.");
            WriteReports(report);
            PublishSummary(report);
            Progress = 100;
            Status = $"{report.Counters.FilesWritten:N0} files recovered.";
        }
        catch (OperationCanceledException)
        {
            Append(LogLevel.Warning, "Cancelled by the user.");
            Status = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or LocalBlobCipher.WrongPasscodeException or UnauthorizedAccessException)
        {
            Append(LogLevel.Error, ex.Message);
            Status = "Extraction failed.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            Raise(nameof(StartCommand));
            Raise(nameof(CancelCommand));
            Raise(nameof(OpenReportCommand));
        }
    }

    private LocalKey LoadLocalKey()
    {
        var notes = new List<string>();
        var key = LocalKeyLoader.Load(TdataPath, Passcode, notes.Add);
        foreach (var note in notes)
        {
            Append(LogLevel.Detail, note);
        }

        Append(LogLevel.Success, $"Local key loaded ({key.Length} bytes).");
        return key;
    }

    private ExtractionOptions BuildOptions() => new()
    {
        OutputDirectory = OutputPath,
        Accounts = [.. SplitList(Accounts)],
        Databases = [.. new[] { IncludeCache ? "cache" : null, IncludeMediaCache ? "media_cache" : null }
            .Where(static n => n is not null).Select(static n => n!)],
        MinimumSize = ParseSize(MinimumSize),
        MaximumSize = ParseSize(MaximumSize),
        KeepUnknownContent = KeepUnknown,
        MinimumCoverage = MinimumCoverage,
        RequireStreamHeader = RequireStreamHeader,
        ScanOrphans = ScanOrphans,
        ComputeHashes = ComputeHashes,
        DatedFileNames = DatedNames,
        MarkPartialFiles = MarkPartials,
        WriteManifest = true,
        WriteJsonManifest = WriteJsonManifest,
        WriteHtmlReport = WriteHtmlReport,
        MaxDegreeOfParallelism = Threads,
        CancellationToken = _cancellation?.Token ?? CancellationToken.None,
        Log = message => Append(Verbose ? LogLevel.Detail : LogLevel.Info, message),
        Progress = new Progress<ExtractionProgress>(OnProgress),
    };

    private void OnProgress(ExtractionProgress progress)
    {
        Progress = progress.DatabasesTotal == 0
            ? 0
            : 100.0 * progress.DatabasesDone / progress.DatabasesTotal;
        _currentBytes = progress.BytesWritten;
        Raise(nameof(BytesWritten));
        Status = $"{progress.CurrentDatabase}: {progress.FilesWritten:N0} files, {Format(progress.BytesWritten)}.";
    }

    private void WriteReports(ExtractionReport report)
    {
        Directory.CreateDirectory(OutputPath);
        CsvManifestWriter.WriteCsv(Path.Combine(OutputPath, "manifest.csv"), report);
        CsvManifestWriter.WriteSkippedCsv(Path.Combine(OutputPath, "skipped.csv"), report);

        if (WriteJsonManifest)
        {
            JsonManifestWriter.WriteJson(Path.Combine(OutputPath, "manifest.json"), report);
        }

        if (WriteHtmlReport)
        {
            HtmlReportWriter.Write(Path.Combine(OutputPath, "index.html"), report);
        }
    }

    private void PublishSummary(ExtractionReport report)
    {
        var filesByDatabase = report.Items
            .GroupBy(static i => (i.Account, i.Database))
            .ToDictionary(
                static g => $"{g.Key.Account}/{g.Key.Database}",
                static g => (Files: (long)g.Count(), Bytes: g.Sum(static i => i.Size)));

        Databases.Clear();
        foreach (var database in report.Databases)
        {
            var label = $"{database.Account}/{database.Database}";
            filesByDatabase.TryGetValue(label, out var stats);

            Databases.Add(new DatabaseSummary(
                label,
                database.Version,
                database.LiveEntries,
                stats.Files,
                stats.Bytes,
                string.Join("; ", database.Warnings)));
        }

        Summary = string.Join(
            Environment.NewLine,
            $"Recovered {report.Counters.FilesWritten:N0} files ({Format(report.Counters.BytesWritten)}) "
                + $"from {report.Counters.EntriesSeen:N0} cache entries in "
                + $"{report.Duration.TotalSeconds:0.0}s.",
            $"Duplicates skipped : {report.Counters.Duplicates:N0}",
            $"Not recognised     : {Skipped(report, SkipReason.NotRecognisedMedia):N0}",
            $"Mostly missing     : {Skipped(report, SkipReason.MostlyMissing):N0}",
            $"Orphan candidates  : {report.Counters.OrphanCandidates:N0}",
            string.Empty,
            string.Join(Environment.NewLine,
                report.ByCategory
                    .OrderByDescending(static p => p.Value)
                    .Select(c => $"  {c.Key,-12} {c.Value,7:N0} files  {Format(report.BytesByCategory[c.Key]),12}")));
    }

    private static long Skipped(ExtractionReport report, SkipReason reason) =>
        report.Counters.Skipped.TryGetValue(reason, out var value) ? value : 0;

    private List<LogEntry> DescribeFolder()
    {
        var entries = new List<LogEntry>();
        var key = LoadLocalKey();

        foreach (var database in CacheDatabaseLocator.Discover(TdataPath))
        {
            entries.Add(new LogEntry(
                DateTimeOffset.Now, LogLevel.Info, $"{database}:"));

            foreach (var version in database.Versions())
            {
                try
                {
                    var parsed = CacheBinlogReader.ReadFile(version.BinlogPath, key, version.Number);
                    entries.Add(new LogEntry(
                        DateTimeOffset.Now,
                        LogLevel.Info,
                        $"   version {version.Number}: {parsed.Entries.Count:N0} live entries, "
                            + $"{parsed.RecordsRead:N0} records"
                            + (parsed.Header.TrackEstimatedTime ? ", timestamps present" : string.Empty)
                            + (parsed.Truncated ? $" (stopped at byte {parsed.StoppedAtOffset:N0})" : string.Empty)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    entries.Add(new LogEntry(
                        DateTimeOffset.Now, LogLevel.Warning, $"   version {version.Number}: {ex.Message}"));
                }
            }
        }

        return entries;
    }

    private void BrowseForFolder(string current, Action<string> assign)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder",
            InitialDirectory = Directory.Exists(current) ? current : null,
        };

        if (dialog.ShowDialog() == true)
        {
            assign(dialog.FolderName);
        }
    }

    private void Append(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        if (_dispatcher.CheckAccess())
        {
            AddEntry(entry);
        }
        else
        {
            _dispatcher.BeginInvoke(() => AddEntry(entry));
        }
    }

    private void AddEntry(LogEntry entry)
    {
        Log.Add(entry);
        while (Log.Count > 5000)
        {
            Log.RemoveAt(0);
        }
    }

    private static IEnumerable<string> SplitList(string value) =>
        value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static long ParseSize(string text) =>
        ByteSize.TryParse(text, out var bytes) ? bytes : 0;

    internal static string Format(long bytes) => ByteSize.Format(bytes);

    private static string DefaultTdata()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < 4 && probe is not null; level++, probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "tdata");
            if (File.Exists(Path.Combine(candidate, "key_datas")))
            {
                return candidate;
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "tdata");
    }

    private static string DefaultOutput() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TelegramMediaRecovery");
}
