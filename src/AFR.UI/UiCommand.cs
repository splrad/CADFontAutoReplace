using System;
using System.Windows.Input;

namespace AFR.UI;

internal sealed class UiRelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute;
    private readonly Func<bool>? _canExecute = canExecute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class UiDialogCloseRequestedEventArgs(bool? dialogResult) : EventArgs
{
    public bool? DialogResult { get; } = dialogResult;
}
