using AFR.Deployer.Models;
using Microsoft.Win32;
using System.IO;

namespace AFR.Deployer.Services;

/// <summary>
/// 插件卸载服务：删除物理 DLL 文件（若存在）并清理注册表自动加载条目。
/// </summary>
internal static class PluginUninstaller
{
    /// <summary>
    /// 卸载指定 CAD 配置文件实例中的插件。
    /// <para>
    /// 流程：
    /// <list type="number">
    ///   <item>重新读取注册表 LOADER 值，获取 DLL 实际路径（防手动修改）。</item>
    ///   <item>若 DLL 文件存在则尝试删除；若已被手动删除则跳过文件操作。</item>
    ///   <item>删除注册表中该配置文件实例下的 AppName 子键。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="installation">目标 CAD 配置文件实例（来自最新一次扫描结果）。</param>
    /// <param name="warningMessage">部分成功时的警告（如删除文件失败但注册表已清理），完全成功时为 null。</param>
    /// <returns>true 表示注册表项已成功清理（文件操作失败不影响返回值）。</returns>
    internal static bool TryUninstall(CadInstallation installation, out string? warningMessage)
    {
        warningMessage = null;

        // 1. 重新从注册表读取 LOADER，防止 UI 缓存与实际不符
        string? dllPath;
        try
        {
            using var appKey = Registry.CurrentUser.OpenSubKey(installation.RegistryAppPath, false);
            dllPath = appKey?.GetValue("LOADER") as string;
        }
        catch (Exception ex)
        {
            warningMessage = $"读取注册表失败：{ex.Message}";
            return false;
        }

        // 2. 删除 DLL 文件（若存在）
        if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
        {
            try
            {
                File.Delete(dllPath);
            }
            catch (Exception ex)
            {
                // 文件删除失败（如权限不足、文件被占用）：记录警告但继续清理注册表
                warningMessage = $"DLL 文件删除失败（{ex.Message}），注册表条目仍将被清理。";
            }
        }

        // 3. 删除注册表 AppName 子键
        // RegistryAppPath 末尾为 "...\Applications\{AppName}"，直接用已知的 AppName 定位
        try
        {
            var applicationsPath = installation.RegistryAppPath[
                ..^(installation.Descriptor.AppName.Length + 1)]; // 去掉 "\AppName"
            using var parentKey = Registry.CurrentUser.OpenSubKey(applicationsPath, true);
            parentKey?.DeleteSubKeyTree(installation.Descriptor.AppName, throwOnMissingSubKey: false);

            return true;
        }
        catch (Exception ex)
        {
            warningMessage = (warningMessage is null ? "" : warningMessage + " | ")
                           + $"删除注册表项失败：{ex.Message}";
            return false;
        }
    }
}
