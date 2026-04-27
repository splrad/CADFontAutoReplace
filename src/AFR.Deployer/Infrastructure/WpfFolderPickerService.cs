using Microsoft.Win32;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// <see cref="IFolderPickerService"/> 的 WPF 实现，委托给 <see cref="OpenFolderDialog"/>。
/// </summary>
internal sealed class WpfFolderPickerService : IFolderPickerService
{
    /// <inheritdoc />
    public string? PickFolder(string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title            = "选择插件 DLL 的释放目录",
            InitialDirectory = initialDirectory,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
