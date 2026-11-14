using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TelegramMediaRecovery.Desktop.ViewModels;

namespace TelegramMediaRecovery.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _positionTimer;
    private bool _updatingPosition;
    private bool _allowClose;
    private bool _closingInProgress;
    private bool _closed;

    public MainWindow() : this(new MainViewModel(Dispatcher.CurrentDispatcher)) { }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => FitToWorkArea();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
            }
            else if (e.Key == Key.Escape && viewModel.CancelCommand.CanExecute(null))
            {
                viewModel.CancelCommand.Execute(null); e.Handled = true;
            }
        };
        viewModel.ChooseExportFolder = () => PickFolder("Choose an export location (a new folder is created for each export)");
        viewModel.OpenRequested += OpenPath;
        viewModel.PreviewChanging += ClosePlayer;
        viewModel.PropertyChanged += ViewModelChanged;
        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => UpdatePosition(), Dispatcher);
        Loaded += (_, _) => _positionTimer.Start();
        Closing += async (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                if (_closingInProgress) { return; }
                _closingInProgress = true;
                IsEnabled = false;
                await viewModel.CancelAndWaitAsync();
                if (_closed || Dispatcher.HasShutdownStarted) { return; }
                // Cleanup can complete synchronously. Always leave the current Closing event before closing again.
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (_closed) { return; }
                    _allowClose = true;
                    Close();
                }));
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _positionTimer.Stop();
            ClosePlayer();
            viewModel.Dispose();
            viewModel.PropertyChanged -= ViewModelChanged;
            viewModel.OpenRequested -= OpenPath;
            viewModel.PreviewChanging -= ClosePlayer;
        };
    }

    private void OnBrowseSource(object sender, RoutedEventArgs e)
    {
        var folder = PickFolder("Choose tdata or the Telegram Desktop directory");
        if (folder is not null) { _viewModel.SourcePath = folder; }
    }
    private void FitToWorkArea()
    {
        var available = SystemParameters.WorkArea;
        var width = Math.Max(320, available.Width - 24);
        var height = Math.Max(320, available.Height - 24);
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        Width = Math.Min(Width, width);
        Height = Math.Min(Height, height);
    }
    private string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
    private void OnPasscodeChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) { viewModel.Passcode = PasscodeBox.Password; }
    }
    private async void OnMediaRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaItemViewModel item }) { await _viewModel.EnsureThumbnailAsync(item); }
    }
    private async void OnMediaRowContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MediaItemViewModel previous && !ReferenceEquals(e.OldValue, e.NewValue)) { _viewModel.ReleaseThumbnail(previous); }
        if (sender is FrameworkElement { IsLoaded: true } && e.NewValue is MediaItemViewModel item) { await _viewModel.EnsureThumbnailAsync(item); }
    }
    private void OnPreviewPaneSizeChanged(object sender, SizeChangedEventArgs e) =>
        PreviewSurface.Height = Math.Clamp(e.NewSize.Height * 0.42, 140, 300);
    private void OnMediaRowUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaItemViewModel item }) { _viewModel.ReleaseThumbnail(item); }
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewUri))
        {
            ClosePlayer();
            if (_viewModel.PreviewUri is { } uri) { Player.Source = uri; }
        }
    }
    private void ClosePlayer()
    {
        Player.Close();
        Player.Source = null;
        _updatingPosition = true;
        Seek.Value = 0;
        Seek.Maximum = 1;
        _updatingPosition = false;
    }
    private void OnPlay(object sender, RoutedEventArgs e) => Player.Play();
    private void OnPause(object sender, RoutedEventArgs e) => Player.Pause();
    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (!HasCurrentPlayerSource()) { return; }
        if (Player.NaturalDuration.HasTimeSpan) { Seek.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds; }
    }
    private void OnMediaEnded(object sender, RoutedEventArgs e) { Player.Stop(); }
    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!HasCurrentPlayerSource()) { return; }
        _viewModel.PreviewMessage = "The Windows player cannot preview this format. Open it in an external player or export it.";
    }
    private void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingPosition && Player.NaturalDuration.HasTimeSpan) { Player.Position = TimeSpan.FromSeconds(e.NewValue); }
    }
    private void UpdatePosition()
    {
        if (!HasCurrentPlayerSource() || !Player.NaturalDuration.HasTimeSpan) { return; }
        _updatingPosition = true;
        Seek.Value = Player.Position.TotalSeconds;
        _updatingPosition = false;
    }
    private bool HasCurrentPlayerSource() => !_closingInProgress && _viewModel.PreviewUri is { } uri && Player.Source == uri;
    private void OnOpenPreview(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PreviewUri is { IsFile: true } uri) { OpenPath(uri.LocalPath); }
    }
    private void OpenPath(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            _viewModel.PreviewMessage = "Could not open: " + ex.Message;
        }
    }
}
