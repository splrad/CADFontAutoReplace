using System;
using System.Windows.Input;

namespace AFR.UI;

internal sealed class UiRelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public UiRelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class UiDialogCloseRequestedEventArgs : EventArgs
{
    public bool? DialogResult { get; }

    public UiDialogCloseRequestedEventArgs(bool? dialogResult)
    {
        DialogResult = dialogResult;
    }
}
