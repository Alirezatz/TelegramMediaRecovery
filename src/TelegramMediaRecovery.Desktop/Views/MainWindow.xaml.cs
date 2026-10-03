using System.Diagnostics;
using System.IO;
using System.Windows;
using TelegramMediaRecovery.Desktop.ViewModels;

namespace TelegramMediaRecovery.Desktop.Views;

/// <summary>
/// The single window of the app: settings on the left, log / databases / summary
/// on the right.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window and wires it to its view model.</summary>
    public MainWindow()
    {
        InitializeComponent();

        var viewModel = new MainViewModel(Dispatcher);
        DataContext = viewModel;
        viewModel.OpenFolderRequested += OnOpenFolderRequested;
    }

    private void OnBrowseTdata(object sender, RoutedEventArgs e) =>
        ViewModel.BrowseTdataCommand.Execute(null);

    private void OnBrowseOutput(object sender, RoutedEventArgs e) =>
        ViewModel.BrowseOutputCommand.Execute(null);

    private void OnStart(object sender, RoutedEventArgs e)
    {
        ViewModel.Passcode = PasscodeBox.Password;
        ViewModel.StartCommand.Execute(null);
    }

    private void OnScan(object sender, RoutedEventArgs e)
    {
        ViewModel.Passcode = PasscodeBox.Password;
        ViewModel.ScanCommand.Execute(null);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.CancelCommand.Execute(null);

    private void OnOpenOutput(object sender, RoutedEventArgs e) => ViewModel.OpenOutputCommand.Execute(null);

    private void OnOpenReport(object sender, RoutedEventArgs e) => ViewModel.OpenReportCommand.Execute(null);

    private void OnOpenFolderRequested(object? sender, string target)
    {
        try
        {
            if (File.Exists(target))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }

            if (Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    ArgumentList = { target },
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(ex.Message, "Telegram Media Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;
}
