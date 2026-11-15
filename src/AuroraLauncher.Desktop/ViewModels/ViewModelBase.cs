using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuroraCore.Services;

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

public class RelayCommand : System.Windows.Input.ICommand, INotifyPropertyChanged
{
    private readonly Func<object?, Task>? _async;
    private readonly Action<object?>? _sync;
    private readonly Func<object?, bool>? _can;
    private readonly CommandGate? _gate;

    /// <summary>Sync command: it finishes before the next click can be
    /// processed, so re-entrancy is impossible and no gate is allocated.</summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null, bool guardReentry = false)
    {
        _sync = execute;
        _can = canExecute;
        _gate = guardReentry ? new CommandGate() : null;
    }

    /// <summary>
    /// Async command. Round-93: <paramref name="guardReentry"/> defaults to
    /// <c>true</c> — while the awaited work runs, <see cref="CanExecute"/>
    /// returns false, so WPF greys the button out and an impatient second click
    /// is dropped instead of starting the same work twice.
    /// </summary>
    public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null, bool guardReentry = true)
    {
        _async = executeAsync;
        _can = canExecute;
        _gate = guardReentry ? new CommandGate() : null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the guarded execution is in flight. Bindable, so a
    /// button's caption can switch to「扫描中…」without a VM property per button.</summary>
    public bool IsRunning => _gate?.IsRunning ?? false;

    /// <summary>Clicks dropped because the previous run had not finished.
    /// Surfaced for diagnostics — a user complaint about "点不动" is otherwise
    /// impossible to confirm.</summary>
    public int SuppressedClicks => _gate?.SuppressedClicks ?? 0;

    public bool CanExecute(object? parameter)
    {
        if (_gate?.IsRunning == true) return false;
        return _can == null || _can(parameter);
    }

    public async void Execute(object? parameter)
    {
        if (_gate != null && !_gate.TryEnter())
        {
            AuroraCore.Utils.Logger.Instance.Trace("Cmd",
                "忽略重复点击：上一次操作还没结束（" + (_sync != null ? "sync" : "async") + "）");
            return;
        }
        if (_gate != null) NotifyRunningChanged();

        try
        {
            if (_sync != null) _sync(parameter);
            else if (_async != null) await _async(parameter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Round-93: this used to be Debug.WriteLine only — a silent black
            // hole that made "nothing happened" impossible to diagnose.
            AuroraCore.Utils.Logger.Instance.Error("Cmd", "命令执行失败: " + ex);
        }
        finally
        {
            if (_gate != null)
            {
                _gate.Exit();
                NotifyRunningChanged();
            }
        }
    }

    /// <summary>CanExecuteChanged/PropertyChanged must be raised on the UI
    /// thread: some callers await with ConfigureAwait(false), so the finally
    /// block can run on a pool thread.</summary>
    private void NotifyRunningChanged()
    {
        void Raise()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); } catch { /* handler gone */ }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Raise();
        else
        {
            // The app may be shutting down while a command finishes; a dead
            // dispatcher must not throw out of a finally block.
            try { dispatcher.BeginInvoke(new Action(Raise)); } catch { }
        }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        else
        {
            try { dispatcher.BeginInvoke(new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty))); }
            catch { }
        }
    }
}
