using System.Windows;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// <see cref="IDialogService"/> 的 WPF 实现，统一通过 <see cref="MessageBox"/> 显示。
/// </summary>
internal sealed class WpfDialogService : IDialogService
{
    private readonly Window _owner;

    internal WpfDialogService(Window owner) => _owner = owner;

    public System.Threading.Tasks.Task ShowInfoAsync(string message, string title)
    {
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task ShowWarningAsync(string message, string title)
    {
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task<bool> ConfirmAsync(string message, string title)
    {
        var result = MessageBox.Show(_owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        return System.Threading.Tasks.Task.FromResult(result == MessageBoxResult.OK);
    }
}
