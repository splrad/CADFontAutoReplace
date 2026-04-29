using System.Threading.Tasks;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// 对话框服务接口。WinUI 3 的 ContentDialog 必须异步使用。
/// </summary>
internal interface IDialogService
{
    /// <summary>显示信息提示对话框。</summary>
    Task ShowInfoAsync(string message, string title);

    /// <summary>显示警告对话框。</summary>
    Task ShowWarningAsync(string message, string title);

    /// <summary>显示需要用户确认的对话框。</summary>
    /// <returns>true 表示用户点击了"确定"。</returns>
    Task<bool> ConfirmAsync(string message, string title);
}
