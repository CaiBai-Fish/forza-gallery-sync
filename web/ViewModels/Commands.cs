using System.Windows.Input;

namespace ForzaGallerySync.ViewModels;

/// <summary>同步命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>异步命令（捕获异常回调）。</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>命令执行失败的异常回调（页面/VM 用于展示错误）。</summary>
    public Action<Exception>? OnError { get; set; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
