using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace Sluice.Desktop.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        CommandRequery.Invalidate();
    }
}

/// <summary>
/// Stand-in for WPF's CommandManager.RequerySuggested: commands re-check CanExecute after any view-model change
/// (debounced to one pass per UI-thread turn).
/// </summary>
public static class CommandRequery
{
    private static int _pending;

    public static event EventHandler? Requery;

    public static void Invalidate()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            Requery?.Invoke(null, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.Requery += value;
        remove => CommandRequery.Requery -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.Requery += value;
        remove => CommandRequery.Requery -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandRequery.Invalidate();
        try { await execute(); }
        finally
        {
            _running = false;
            CommandRequery.Invalidate();
        }
    }
}
