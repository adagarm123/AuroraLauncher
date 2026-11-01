using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AuroraLauncher.ViewModels;

public class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Func<object?, Task>? _async;
    private readonly Action<object?>? _sync;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    { _sync = execute; _can = canExecute; }

    public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    { _async = executeAsync; _can = canExecute; }

    public bool CanExecute(object? parameter) => _can == null || _can(parameter);

    public async void Execute(object? parameter)
    {
        if (_sync != null) _sync(parameter);
        else if (_async != null) { try { await _async(parameter); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); } }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
