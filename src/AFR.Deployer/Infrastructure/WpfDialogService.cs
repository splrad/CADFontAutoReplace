using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// <see cref="IDialogService"/> 的 WPF-UI 实现，使用 <see cref="Wpf.Ui.Controls.MessageBox"/>，
/// 与主窗口风格保持一致（Fluent 主题、可主题化）。
/// </summary>
internal sealed class WpfDialogService : IDialogService
{
    private readonly Window _owner;

    internal WpfDialogService(Window owner) => _owner = owner;

    public async Task ShowInfoAsync(string message, string title)
    {
        var box = new WpfUiMessageBox
        {
            Title                 = title,
            Content               = message,
            CloseButtonText       = "确定",
            CloseButtonAppearance = ControlAppearance.Primary,
            Owner                 = _owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        await box.ShowDialogAsync();
    }

    public async Task ShowWarningAsync(string message, string title)
    {
        var box = new WpfUiMessageBox
        {
            Title                 = title,
            Content               = message,
            CloseButtonText       = "知道了",
            CloseButtonAppearance = ControlAppearance.Caution,
            Owner                 = _owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        await box.ShowDialogAsync();
    }

    public async Task<bool> ConfirmAsync(string message, string title)
    {
        var box = new WpfUiMessageBox
        {
            Title                   = title,
            Content                 = message,
            PrimaryButtonText       = "确定",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText         = "取消",
            Owner                   = _owner,
            WindowStartupLocation   = WindowStartupLocation.CenterOwner,
        };
        var result = await box.ShowDialogAsync();
        return result == WpfUiMessageBoxResult.Primary;
    }
}
