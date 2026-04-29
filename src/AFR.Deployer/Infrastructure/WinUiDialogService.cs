using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// <see cref="IDialogService"/> 的 WinUI 3 实现，统一使用 <see cref="ContentDialog"/>。
/// </summary>
internal sealed class WinUiDialogService : IDialogService
{
    private readonly Window _window;

    internal WinUiDialogService(Window window) => _window = window;

    public Task ShowInfoAsync(string message, string title)
        => ShowAsync(title, message, "确定", null);

    public Task ShowWarningAsync(string message, string title)
        => ShowAsync(title, message, "确定", null);

    public async Task<bool> ConfirmAsync(string message, string title)
    {
        var result = await ShowAsync(title, message, "确定", "取消");
        return result == ContentDialogResult.Primary;
    }

    private Task<ContentDialogResult> ShowAsync(string title, string message, string primary, string? secondary)
    {
        var dialog = new ContentDialog
        {
            Title             = title,
            Content           = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = _window.Content.XamlRoot,
        };
        if (secondary is not null)
            dialog.CloseButtonText = secondary;

        return dialog.ShowAsync().AsTask();
    }
}
