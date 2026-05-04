using AFR.Deployer.Models;
using Microsoft.Win32;
using System.IO;

namespace AFR.Deployer.Services;

/// <summary>
/// 插件安装服务：将嵌入的插件 DLL 提取到目标目录，并写入注册表自动加载条目。
/// </summary>
internal static class PluginDeployer
{
    private const string Description = "AFR Auto Replace Font Plugin";
    private const int    LoadCtrls   = 2;   // 随 AutoCAD 启动自动加载
    private const int    Managed     = 1;   // .NET 托管插件

    /// <summary>
    /// 安装指定 CAD 版本的插件。
    /// <para>
    /// 流程：
    /// 1) 提取 DLL；
    /// 2) 通过 <see cref="PluginMetadataReader"/> 从 DLL 中读取版本号、构建标识，
    ///    以及 DLL 自我描述的注册表默认值清单（<c>RegistryDefaultString/Dword</c> 程序集级特性）；
    /// 3) 向该 CAD 版本下的所有配置文件写入 AutoCAD 协议键（LOADER / LOADCTRLS / MANAGED / DESCRIPTION）以及插件版本标识
    ///    （PluginVersion / PluginBuildId）；
    /// 4) 按 DLL 声明的清单写入默认值，仅当注册表中尚不存在该值时才写入，避免覆盖用户自定义。
    /// </para>
    /// <para>
    /// 升级 DLL 时若新增/修改/删除注册表项，仅需调整插件侧的 <c>[assembly: RegistryDefault*]</c> 声明，
    /// 部署工具自动跟随，不再硬编码键名常量。
    /// </para>
    /// </summary>
    /// <param name="installation">目标 CAD 版本（来自最新一次扫描结果）。</param>
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

        // 2. 从 DLL 中读取元数据（版本、构建标识、注册表默认值清单）。
        if (!PluginMetadataReader.TryRead(dllPath, out var meta, out errorMessage))
        {
            return false;
        }

        // 3. 写入注册表（幂等）
        try
        {
            foreach (var profileSubKey in installation.ProfileSubKeys)
            {
                using var key = Registry.CurrentUser.CreateSubKey(installation.GetRegistryAppPath(profileSubKey), true);

                // 协议键 + 插件版本标识：始终覆写，确保升级后版本/路径与最新 DLL 一致。
                key.SetValue("LOADER",        dllPath,             RegistryValueKind.String);
                key.SetValue("LOADCTRLS",     LoadCtrls,           RegistryValueKind.DWord);
                key.SetValue("MANAGED",       Managed,             RegistryValueKind.DWord);
                key.SetValue("DESCRIPTION",   Description,         RegistryValueKind.String);
                key.SetValue("PluginVersion", meta!.DisplayVersion, RegistryValueKind.String);
                key.SetValue("PluginBuildId", meta.BuildId,        RegistryValueKind.String);

                // DLL 自我描述的默认值。
                // - String/Dword：写到 Applications\<AppName>，仅在缺失时写入；
                // - DwordAt：写到 ProfileSubKey\<SubPath>（典型如 FixedProfile\General Configuration），
                //   按 ForceOverwrite 决定是否覆盖；按 RemoveOnUninstall 在
                //   Applications\<AppName>\__Owned\<SubPath> 下记录所有权标记，仅当卸载时
                //   现值仍等于我们写入的内容才清除——保留用户预设与中途手改。
                foreach (var item in meta.RegistryDefaults)
                {
                    switch (item.Kind)
                    {
                        case PluginMetadataReader.RegistryDefaultKind.String:
                            if (key.GetValue(item.Name) is null)
                                key.SetValue(item.Name, item.StringValue ?? string.Empty, RegistryValueKind.String);
                            break;

                        case PluginMetadataReader.RegistryDefaultKind.Dword:
                            if (key.GetValue(item.Name) is null)
                                key.SetValue(item.Name, item.DwordValue, RegistryValueKind.DWord);
                            break;

                        case PluginMetadataReader.RegistryDefaultKind.DwordAt:
                            WriteDwordAt(installation, profileSubKey, key, item);
                            break;
                    }
                }
            }

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"写入注册表失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 处理 <see cref="PluginMetadataReader.RegistryDefaultKind.DwordAt"/>：
    /// <para>
    /// 在 <c>ProfileSubKey\&lt;SubPath&gt;</c> 下写入 DWORD，并在
    /// <c>Applications\&lt;AppName&gt;\__Owned\&lt;SubPath&gt;</c> 下记录所有权标记。
    /// 写入策略：
    /// <list type="bullet">
    ///   <item><description>不存在 → 写入并打标记。</description></item>
    ///   <item><description>已存在且等于期望值 → 视为用户预设，不打标记，不动数据。</description></item>
    ///   <item><description>已存在但不等于期望值 → 仅当 <c>ForceOverwrite</c> = true 时覆盖，并打标记。</description></item>
    /// </list>
    /// 卸载时仅清理"打过标记且现值仍等于我们写入值"的条目。
    /// </para>
    /// </summary>
    private static void WriteDwordAt(
        CadInstallation installation,
        string profileSubKey,
        RegistryKey appKey,
        PluginMetadataReader.RegistryDefault item)
    {
        var subPath = item.SubPath;
        if (string.IsNullOrEmpty(subPath)) return;

        var profilePath = $@"{installation.GetProfileRootPath(profileSubKey)}\{subPath}";

        try
        {
            using var target = Registry.CurrentUser.CreateSubKey(profilePath, true);
            if (target is null) return;

            var existing = target.GetValue(item.Name);
            bool wrote;
            if (existing is null)
            {
                target.SetValue(item.Name, item.DwordValue, RegistryValueKind.DWord);
                wrote = true;
            }
            else if (existing is int cur && cur == item.DwordValue)
            {
                // 用户预设或我们之前写过——保留原状，不打/重打标记。
                wrote = false;
            }
            else if (item.ForceOverwrite)
            {
                target.SetValue(item.Name, item.DwordValue, RegistryValueKind.DWord);
                wrote = true;
            }
            else
            {
                wrote = false;
            }

            if (wrote && item.RemoveOnUninstall)
            {
                using var ownedRoot = appKey.CreateSubKey(@"__Owned\" + subPath, true);
                ownedRoot?.SetValue(item.Name, item.DwordValue, RegistryValueKind.DWord);
            }
        }
        catch
        {
            // 单条外部键写入失败不应中断整体安装流程。
        }
    }
}
