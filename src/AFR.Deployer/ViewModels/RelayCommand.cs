using System.Windows.Input;

namespace AFR.Deployer.ViewModels;

/// <summary>
/// 通用 <see cref="ICommand"/> 实现，将命令逻辑委托给外部 Action / Func。
/// </summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    internal RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute    = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>手动触发 CanExecute 重新评估。</summary>
    internal void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
