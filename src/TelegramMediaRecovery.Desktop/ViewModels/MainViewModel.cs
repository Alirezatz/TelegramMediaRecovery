using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TelegramMediaRecovery.Core;
using TelegramMediaRecovery.Core.Browsing;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Desktop.ViewModels;

/// <summary>Scan-first media browsing. Cache reading and export decisions live in RecoverySession.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ThumbnailLoader _thumbnails = new();
    private readonly MediaFilter _filter = new();
    private readonly PreviewController _previews;
    private string? _appliedSort;
    private bool _closing;
    private readonly HashSet<MediaItemViewModel> _selected = [];

    private RecoverySession? _session;

    private CancellationTokenSource? _operation;
    private CancellationTokenSource _scanLifetime = new();

    private ObservableCollection<MediaItemViewModel> _items = [];
    private ICollectionView _itemsView;
    private string _sourcePath = string.Empty;
    private string _passcode = string.Empty;
    private string _query = string.Empty;
    private string _kindFilter = "All types";
    private string _usageFilter = "All usage";
    private string _stateFilter = "All states";
    private string _sort = "Cache date: newest first";
    private string _minimumSize = string.Empty;
    private string _maximumSize = string.Empty;
    private DateTime? _dateFrom;
    private DateTime? _dateTo;

    private long _selectedBytes;
    private string _filterError = string.Empty;
    private bool _busy;
    private bool _hasCatalog;
    private bool _batchSelection;
    private bool _disposed;
    private double _progress;
    private string _status = "Choose a Telegram data directory to scan cached media.";
    private string _catalogSummary = "Scanning reads the cache. Choose files to export after browsing.";
    private string _lastOutput = string.Empty;
    private MediaItemViewModel? _current;

    private int _visibleCount;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _previews = new PreviewController(dispatcher);
        _previews.Changing += () => PreviewChanging?.Invoke();
        _previews.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewController.Image)) { Raise(nameof(PreviewImage)); }
            if (e.PropertyName == nameof(PreviewController.Uri)) { Raise(nameof(PreviewUri)); Raise(nameof(HasMediaPreview)); }
            if (e.PropertyName == nameof(PreviewController.Message)) { Raise(nameof(PreviewMessage)); }
        };
        _itemsView = CollectionViewSource.GetDefaultView(_items);
        _appliedSort = null;
        _itemsView.Filter = Matches;
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath));
        ExportCommand = new AsyncRelayCommand(async () =>
        {
            var destination = ChooseExportFolder?.Invoke();
            if (!string.IsNullOrEmpty(destination)) { await ExportSelectedAsync(destination); }
        }, () => !IsBusy && _selected.Count > 0);
        CancelCommand = new RelayCommand(_ => _operation?.Cancel(), _ => IsBusy);
        FindSourcesCommand = new RelayCommand(_ => FindSources(), _ => !IsBusy);
        SelectVisibleCommand = new RelayCommand(_ => CheckVisible(), _ => !IsBusy && VisibleCount > 0);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => !IsBusy && _selected.Count > 0);
        ResetFiltersCommand = new RelayCommand(_ => ResetFilters());
        OpenOutputCommand = new RelayCommand(_ => OpenRequested?.Invoke(LastOutput), _ => Directory.Exists(LastOutput));

    }

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand FindSourcesCommand { get; }
    public RelayCommand SelectVisibleCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ResetFiltersCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public Func<string?>? ChooseExportFolder { get; set; }
    public event Action<string>? OpenRequested;
    public event Action? PreviewChanging;
    public ObservableCollection<string> DetectedSources { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public IReadOnlyList<string> Kinds { get; } = ["All types", "Images", "Videos", "Animated stickers", "Audio", "Documents"];
    public IReadOnlyList<string> Usages { get; } = ["All usage", "Hide avatars and thumbnails", "Media files", "Avatars / icons", "Thumbnails / covers", "Stickers / emoji", "Unknown usage"];
    public IReadOnlyList<string> States { get; } = ["All states", "Cache verified", "Integrity unknown", "Gaps detected"];
    public IReadOnlyList<string> Sorts { get; } = ["Cache date: newest first", "Cache date: oldest first", "Size: largest first", "Size: smallest first", "File type"];
    public ICollectionView ItemsView => _itemsView;
    public IReadOnlyList<MediaItemViewModel> Items => _items;
    public string SourcePath
    {
        get => _sourcePath;
        set { if (!IsBusy && Set(ref _sourcePath, value)) { ResetCatalog(); RefreshCommands(); } }
    }
    public string Passcode
    {
        get => _passcode;
        set { if (!IsBusy && Set(ref _passcode, value)) { ResetCatalog(); } }
    }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(CanEditSource)); RefreshCommands(); } } }
    public bool CanEditSource => !IsBusy;
    public bool HasCatalog { get => _hasCatalog; private set { if (Set(ref _hasCatalog, value)) { Raise(nameof(EmptyMessage)); } } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string CatalogSummary { get => _catalogSummary; private set => Set(ref _catalogSummary, value); }
    public string LastOutput { get => _lastOutput; private set { Set(ref _lastOutput, value); OpenOutputCommand.RaiseCanExecuteChanged(); } }
    public int VisibleCount { get => _visibleCount; private set { Set(ref _visibleCount, value); Raise(nameof(ResultsTitle)); Raise(nameof(EmptyMessage)); } }
    public string ResultsTitle => $"Media  {VisibleCount:N0} / {_items.Count:N0}";
    public string EmptyMessage => !HasCatalog ? "Choose a data source and scan to see its media here."
        : _items.Count == 0 ? "No readable media found. Check the data source and activity log."
        : "No media matches these filters. Clear the filters to try again.";
    public int SelectedCount => _selected.Count;
    public string SelectionSummary
    {
        get
        {
            var hidden = _selected.Count(i => !Matches(i));
            return $"Selected {_selected.Count:N0} items · Estimated {ByteSize.Format(_selectedBytes)}" + (hidden > 0 ? $" ({hidden} hidden by filters)" : string.Empty);
        }
    }
    public string Query { get => _query; set { if (Set(ref _query, value)) { RefreshFilters(); } } }
    public string KindFilter { get => _kindFilter; set { if (Set(ref _kindFilter, value)) { RefreshFilters(); } } }
    public string UsageFilter { get => _usageFilter; set { if (Set(ref _usageFilter, value)) { RefreshFilters(); } } }
    public string StateFilter { get => _stateFilter; set { if (Set(ref _stateFilter, value)) { RefreshFilters(); } } }
    public string Sort { get => _sort; set { if (Set(ref _sort, value)) { RefreshFilters(); } } }
    public string MinimumSize { get => _minimumSize; set { if (Set(ref _minimumSize, value)) { RefreshFilters(); } } }
    public string MaximumSize { get => _maximumSize; set { if (Set(ref _maximumSize, value)) { RefreshFilters(); } } }
    public DateTime? DateFrom { get => _dateFrom; set { if (Set(ref _dateFrom, value)) { RefreshFilters(); } } }
    public DateTime? DateTo { get => _dateTo; set { if (Set(ref _dateTo, value)) { RefreshFilters(); } } }
    public string FilterError { get => _filterError; private set => Set(ref _filterError, value); }
    public MediaItemViewModel? CurrentItem
    {
        get => _current;
        set
        {
            if (Set(ref _current, value))
            {
                Raise(nameof(HasCurrentItem));
                Raise(nameof(CurrentDetails));
                if (!_closing && value is not null && _session is not null) { CurrentPreview = _previews.ShowAsync(_session, value, _scanLifetime.Token); }
                else { _previews.Clear(); CurrentPreview = Task.CompletedTask; }
            }
        }
    }
    public Task CurrentPreview { get; private set; } = Task.CompletedTask;
    public bool HasCurrentItem => CurrentItem is not null;
    public BitmapSource? PreviewImage => _previews.Image;
    public Uri? PreviewUri => _previews.Uri;
    public bool HasMediaPreview => PreviewUri is not null;
    public string PreviewMessage { get => _previews.Message; set => _previews.Message = value; }
    public string CurrentDetails => CurrentItem is not { } item ? string.Empty
        : $"Type  {item.Kind} · {item.Extension}\nUsage  {item.Usage}\nKnown size  {item.Size}\nEstimated cache time  {item.DateLabel}\nAccount  {item.Item.Account}\nSource  {(item.Item.IsStream ? "streaming media cache" : "media cache")}\nCached fragments  {item.Item.Fragments}\nStatus  {item.State}\nSaving restrictions  Unknown (source message not linked)";

    public void FindSources()
    {
        if (IsBusy) { return; }
        DetectedSources.Clear();
        foreach (var path in SourceDiscovery.Find()) { DetectedSources.Add(path); }
        if (DetectedSources.Count == 1) { SourcePath = DetectedSources[0]; }
        Status = DetectedSources.Count == 0 ? "No data directories found. Choose tdata or its parent directory." : $"Found {DetectedSources.Count} data sources. Select one to scan.";
    }

    public async Task ScanAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SourcePath)) { return; }
        ResetCatalog();
        using var operation = BeginOperation("Opening data source...");
        var source = SourcePath;
        var passcode = Passcode;
        RecoverySession? session = null;
        try
        {
            var catalog = await Task.Run(() =>
            {
                session = new RecoverySession(source, passcode);
                return session.Scan(CreateProgress(), operation.Token);
            });
            operation.Token.ThrowIfCancellationRequested();
            _session = session;
            session = null;
            SetItems(catalog.Items.Select(i => new MediaItemViewModel(i)));
            HasCatalog = true;
            CatalogSummary = $"Found {_items.Count:N0} media · Avatars/icons {_items.Count(i => i.Item.Usage == CacheUsage.ProfilePhoto):N0} · Thumbnails/covers {_items.Count(i => i.Item.Usage is CacheUsage.DocumentThumbnail or CacheUsage.StickerSetThumbnail or CacheUsage.AudioAlbumCover):N0} · {catalog.UnreadableValues} unreadable · {catalog.UnknownValues} unrecognized";
            foreach (var warning in catalog.Warnings) { Append(warning); }
            Status = catalog.Warnings.Count == 0 ? "Scan complete. Browse media, check items and export your selection." : $"Scan complete with {catalog.Warnings.Count} index warnings. See the activity log.";
            Progress = 100;
            Append(CatalogSummary);
        }
        catch (OperationCanceledException) { Status = "Scan cancelled."; }
        catch (Exception ex) when (IsExpected(ex)) { Status = "Scan failed. Check the data directory and local passcode."; Append(ex.Message); }
        finally { session?.Dispose(); EndOperation(); }
    }

    public async Task ExportSelectedAsync(string destination)
    {
        if (IsBusy || _session is null || _selected.Count == 0) { return; }
        var session = _session;
        var selection = _selected.Select(i => i.Item).ToArray();
        using var operation = BeginOperation($"Exporting {selection.Length:N0} items...");
        try
        {
            var result = await Task.Run(() => session.Export(selection, destination, CreateProgress(), operation.Token));
            LastOutput = result.Report.OutputDirectory;
            Status = $"{(result.Cancelled ? "Export cancelled" : "Export complete")}: saved {result.Report.Items.Count:N0} items, {result.Failures.Count} failed.";
            foreach (var failure in result.Failures) { Append(failure); }
            Append(Status + " " + LastOutput);
            Progress = result.Cancelled ? Progress : 100;
        }
        catch (OperationCanceledException) { Status = "Export cancelled."; }
        catch (Exception ex) when (IsExpected(ex)) { Status = "Export failed. See the activity log."; Append(ex.Message); }
        finally { EndOperation(); }
    }

    public Task EnsureThumbnailAsync(MediaItemViewModel item) => _closing || _session is null ? Task.CompletedTask
        : _thumbnails.EnsureAsync(item, _session, _scanLifetime.Token);
    public void ReleaseThumbnail(MediaItemViewModel item) => _thumbnails.Release(item);
    private bool Matches(object value) => value is MediaItemViewModel item && _filter.Matches(item);

    private void RefreshFilters()
    {
        _filter.Update(KindFilter, UsageFilter, StateFilter, Query, MinimumSize, MaximumSize, DateFrom, DateTo);
        FilterError = _filter.Error;
        if (_appliedSort != Sort)
        {
            using (_itemsView.DeferRefresh())
            {
                _itemsView.SortDescriptions.Clear();
                var property = Sort.StartsWith("Size", StringComparison.Ordinal) ? nameof(MediaItemViewModel.Bytes)
                    : Sort == "File type" ? nameof(MediaItemViewModel.Extension) : nameof(MediaItemViewModel.Date);
                var direction = Sort is "Cache date: oldest first" or "Size: smallest first" or "File type" ? ListSortDirection.Ascending : ListSortDirection.Descending;
                _itemsView.SortDescriptions.Add(new SortDescription(property, direction));
                _appliedSort = Sort;
            }
        }
        else { _itemsView.Refresh(); }
        VisibleCount = _itemsView is CollectionView view ? view.Count : _itemsView.Cast<object>().Count();
        Raise(nameof(SelectionSummary));
        SelectVisibleCommand.RaiseCanExecuteChanged();
    }
    private void SetItems(IEnumerable<MediaItemViewModel> items)
    {
        foreach (var item in _items) { item.PropertyChanged -= ItemChanged; }
        _items = new ObservableCollection<MediaItemViewModel>(items);
        foreach (var item in _items) { item.PropertyChanged += ItemChanged; }
        _itemsView = CollectionViewSource.GetDefaultView(_items);
        _appliedSort = null;
        _itemsView.Filter = Matches;
        Raise(nameof(ItemsView));
        Raise(nameof(Items));
        RefreshFilters();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MediaItemViewModel.IsChecked) || sender is not MediaItemViewModel item) { return; }
        if (item.IsChecked && _selected.Add(item)) { _selectedBytes += item.Bytes; }
        else if (!item.IsChecked && _selected.Remove(item)) { _selectedBytes -= item.Bytes; }
        if (!_batchSelection) { SelectionChanged(); }
    }
    private void CheckVisible()
    {
        _batchSelection = true;
        foreach (var item in _itemsView.Cast<MediaItemViewModel>()) { item.IsChecked = true; }
        _batchSelection = false;
        SelectionChanged();
    }
    private void ClearSelection()
    {
        _batchSelection = true;
        foreach (var item in _selected.ToArray()) { item.IsChecked = false; }
        _batchSelection = false;
        SelectionChanged();
    }
    private void SelectionChanged()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(SelectionSummary));
        RefreshCommands();
    }
    private void ResetFilters()
    {
        _query = _minimumSize = _maximumSize = string.Empty;
        _kindFilter = "All types";
        _usageFilter = "All usage";
        _stateFilter = "All states";
        _dateFrom = _dateTo = null;
        foreach (var property in new[] { nameof(Query), nameof(MinimumSize), nameof(MaximumSize), nameof(KindFilter), nameof(UsageFilter), nameof(StateFilter), nameof(DateFrom), nameof(DateTo) }) { Raise(property); }
        RefreshFilters();
    }
    private CancellationTokenSource BeginOperation(string status)
    {
        _operation = new CancellationTokenSource();
        Status = status;
        Progress = 0;
        IsBusy = true;
        return _operation;
    }
    private void EndOperation() { _operation = null; IsBusy = false; }
    private IProgress<RecoveryProgress> CreateProgress()
    {
        var operation = _operation;
        return new Progress<RecoveryProgress>(progress => _dispatcher.BeginInvoke(() =>
        {
            if (!IsBusy || !ReferenceEquals(operation, _operation)) { return; }
            var percent = progress.Total == 0 ? 0 : 100.0 * progress.Completed / progress.Total;
            if (percent < Progress) { return; }
            Progress = percent;
            Status = progress.Phase == "export" ? $"Exporting {progress.Completed:N0} / {progress.Total:N0} items..."
                : $"Scanning {progress.Phase} · {progress.Completed:N0}/{progress.Total:N0} · {progress.Files:N0} media found";
        }));
    }
    private void RefreshCommands()
    {
        ScanCommand.RaiseCanExecuteChanged(); ExportCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
        FindSourcesCommand.RaiseCanExecuteChanged(); SelectVisibleCommand.RaiseCanExecuteChanged(); ClearSelectionCommand.RaiseCanExecuteChanged();
    }
    private void Append(string message)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 200) { Log.RemoveAt(0); }
    }
    private static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or DllNotFoundException or TypeInitializationException
        or System.Security.Cryptography.CryptographicException or TelegramMediaRecovery.Core.Crypto.LocalBlobCipher.WrongPasscodeException;
    private void ClearPreview() => _previews.Clear();
    private void ResetCatalog()
    {
        ClearPreview();
        _scanLifetime.Cancel(); _scanLifetime.Dispose(); _scanLifetime = new CancellationTokenSource();
        _session?.Dispose(); _session = null;
        _selected.Clear(); _selectedBytes = 0;
        _thumbnails.Reset();
        _current = null; Raise(nameof(CurrentItem)); Raise(nameof(HasCurrentItem)); Raise(nameof(CurrentDetails));
        HasCatalog = false;
        CatalogSummary = "Scanning reads the cache. Choose files to export after browsing.";
        Progress = 0;
        SetItems([]);
        SelectionChanged();
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; _closing = true;
        _operation?.Cancel();
        ClearPreview(); _thumbnails.Reset(); _scanLifetime.Cancel(); _session?.Dispose();
    }

    public async Task CancelAndWaitAsync()
    {
        _closing = true;
        _operation?.Cancel();
        try { await Task.WhenAll(ScanCommand.Execution, ExportCommand.Execution, _previews.StopAsync(), _thumbnails.StopAsync()); }
        finally { if (!_disposed) { _closing = false; } }
    }
}
