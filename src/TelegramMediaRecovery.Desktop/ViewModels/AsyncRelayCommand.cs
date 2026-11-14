using System.Windows.Input;

namespace TelegramMediaRecovery.Desktop.ViewModels;

/// <summary>An awaited, non-reentrant UI command.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();
    public Task Execution { get; private set; } = Task.CompletedTask;
    public Task ExecuteAsync()
    {
        if (!CanExecute(null)) { return Task.CompletedTask; }
        _running = true;
        RaiseCanExecuteChanged();
        return Execution = RunAsync();
    }
    private async Task RunAsync()
    {
        try { await execute(); }
        finally { _running = false; RaiseCanExecuteChanged(); }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
