using System.Threading.Tasks;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// <see cref="IFolderPickerService"/> 的 WPF 实现，使用 .NET 自带的
/// <see cref="Microsoft.Win32.OpenFolderDialog"/>（.NET 8+）。
/// </summary>
internal sealed class WpfFolderPickerService : IFolderPickerService
{
    public Task<string?> PickFolderAsync(string initialDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择安装目录",
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }
}
