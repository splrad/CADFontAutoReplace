namespace AFR.Deployer.Infrastructure;

/// <summary>
/// 文件夹选择服务接口，隔离 ViewModel 对 WPF <c>OpenFolderDialog</c> 的直接依赖。
/// </summary>
internal interface IFolderPickerService
{
    /// <summary>
    /// 打开文件夹选择对话框。
    /// </summary>
    /// <param name="initialDirectory">对话框打开时的初始目录。</param>
    /// <returns>用户选择的路径；用户取消时返回 null。</returns>
    string? PickFolder(string initialDirectory);
}
