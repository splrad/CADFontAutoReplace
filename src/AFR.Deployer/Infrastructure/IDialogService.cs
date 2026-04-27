namespace AFR.Deployer.Infrastructure;

/// <summary>
/// 对话框服务接口，隔离 ViewModel 对 WPF <c>MessageBox</c> 的直接依赖。
/// </summary>
internal interface IDialogService
{
    /// <summary>显示信息提示对话框。</summary>
    void ShowInfo(string message, string title);

    /// <summary>显示警告对话框。</summary>
    void ShowWarning(string message, string title);

    /// <summary>显示需要用户确认的对话框。</summary>
    /// <returns>true 表示用户点击了"确定"。</returns>
    bool Confirm(string message, string title);
}
