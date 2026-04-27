using System.Windows;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// <see cref="IDialogService"/> 的 WPF 实现，委托给 <see cref="MessageBox"/>。
/// </summary>
internal sealed class WpfDialogService : IDialogService
{
    /// <inheritdoc />
    public void ShowInfo(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <inheritdoc />
    public void ShowWarning(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <inheritdoc />
    public bool Confirm(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
           == MessageBoxResult.OK;
}
