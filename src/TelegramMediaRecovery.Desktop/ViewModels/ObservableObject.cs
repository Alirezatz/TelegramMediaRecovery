using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TelegramMediaRecovery.Desktop.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base class so the project needs
/// no MVVM framework package.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for the calling property.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Assigns a field and notifies when the value actually changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }
}
