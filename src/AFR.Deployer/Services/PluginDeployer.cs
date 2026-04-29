using AFR.Deployer.Models;
using Microsoft.Win32;
using System.IO;

namespace AFR.Deployer.Services;

/// <summary>
/// 插件安装服务：将嵌入的插件 DLL 提取到目标目录，并写入注册表自动加载条目。
/// </summary>
internal static class PluginDeployer
{
    private const string Description  = "AFR Auto Replace Font Plugin";
    private const int    LoadCtrls    = 2;   // 随 AutoCAD 启动自动加载
    private const int    Managed      = 1;   // .NET 托管插件

    /// <summary>
    /// 安装指定 CAD 配置文件实例的插件。
    /// <para>
    /// 流程：提取 DLL → 写入注册表 LOADER / LOADCTRLS / MANAGED / DESCRIPTION / PluginVersion。
    /// 不写入 MainFont / BigFont / IsInitialized 等用户配置项，这些由插件首次运行时的 AFR 命令处理。
    /// </para>
    /// </summary>
    /// <param name="installation">目标 CAD 配置文件实例（来自最新一次扫描结果）。</param>
    /// <param name="targetDirectory">DLL 的释放目录。</param>
    /// <param name="errorMessage">失败原因，成功时为 null。</param>
    /// <returns>true 表示安装成功。</returns>
    internal static bool TryInstall(
        CadInstallation installation,
        string targetDirectory,
        out string? errorMessage)
    {
        var descriptor = installation.Descriptor;
        var fileName   = $"{descriptor.AppName}.dll";

        // 1. 提取 DLL
        if (!EmbeddedResourceExtractor.TryExtract(
                descriptor.EmbeddedResourceKey,
                targetDirectory,
                fileName,
                out errorMessage))
        {
            return false;
        }

        var dllPath = Path.Combine(targetDirectory, fileName);

        // 2. 写入注册表（幂等）
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(installation.RegistryAppPath, true);
            key.SetValue("LOADER",        dllPath,                                    RegistryValueKind.String);
            key.SetValue("LOADCTRLS",     LoadCtrls,                                  RegistryValueKind.DWord);
            key.SetValue("MANAGED",       Managed,                                    RegistryValueKind.DWord);
            key.SetValue("DESCRIPTION",   Description,                                RegistryValueKind.String);
            key.SetValue("PluginVersion", DeployerVersionService.GetDisplayVersion(), RegistryValueKind.String);
            key.SetValue("PluginBuildId", DeployerVersionService.GetBuildId(),        RegistryValueKind.String);

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"写入注册表失败：{ex.Message}";
            return false;
        }
    }
}
