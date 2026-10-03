using System.Windows.Input;

namespace TelegramMediaRecovery.Desktop.ViewModels;

/// <summary>
/// A command backed by delegates. Deliberately tiny: the app has four commands
/// and a framework would be more code than it saves.
/// </summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    : ICommand
{
    private readonly Action<object?> _execute = execute;
    private readonly Func<object?, bool>? _canExecute = canExecute;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>Tells the UI to re-query <see cref="CanExecute"/>.</summary>
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
