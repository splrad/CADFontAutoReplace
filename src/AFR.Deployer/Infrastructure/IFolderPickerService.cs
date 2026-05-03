using System.Threading.Tasks;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// 文件夹选择服务接口。
/// </summary>
internal interface IFolderPickerService
{
    /// <summary>
    /// 打开文件夹选择对话框。
    /// </summary>
    /// <param name="initialDirectory">对话框的建议起始目录。</param>
    /// <returns>用户选择的路径；用户取消时返回 null。</returns>
    Task<string?> PickFolderAsync(string initialDirectory);
}
