using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// <see cref="IFolderPickerService"/> 的 WinUI 3 实现。
/// <para>
/// Unpackaged WinUI 应用必须通过 <see cref="InitializeWithWindow.Initialize"/>
/// 把窗口 HWND 注入 Picker，否则会抛 <c>COMException</c>。
/// </para>
/// </summary>
internal sealed class WinUiFolderPickerService : IFolderPickerService
{
    private readonly Window _window;

    internal WinUiFolderPickerService(Window window) => _window = window;

    public async Task<string?> PickFolderAsync(string initialDirectory)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(_window);
        InitializeWithWindow.Initialize(picker, hwnd);

        try
        {
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
