using System.Text.RegularExpressions;
using AFR.Deployer.Models;
using Microsoft.Win32;

namespace AFR.Deployer.Services;

/// <summary>
/// 扫描本机注册表，枚举所有已安装的受支持 CAD 版本及插件的聚合部署状态。
/// <para>
/// 每次调用 <see cref="Scan"/> 都会重新读取注册表，确保反映用户在工具运行期间的手动修改。
/// </para>
/// </summary>
internal static partial class CadRegistryScanner
{
    /// <summary>AutoCAD 配置文件子键的匹配模式，对所有 AutoCAD 版本通用。</summary>
    [GeneratedRegex(@"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$")]
    private static partial Regex ProfilePattern();

    /// <summary>
    /// 扫描注册表，返回所有受支持 CAD 版本的条目列表（按品牌 → 版本排序）。
    /// <para>
    /// 无论本机是否安装某个受支持版本，<see cref="CadDescriptors.All"/> 中的每个版本至少返回
    /// 一条记录：已安装则聚合其全部配置文件子键，未安装则返回单条占位条目
    /// （<see cref="CadInstallation.IsCadInstalled"/> 为 false）。这样 UI 可以列出全部支持版本，
    /// 并对未安装的版本禁用操作。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<CadInstallation> Scan()
    {
        var results = new List<CadInstallation>();

        foreach (var descriptor in CadDescriptors.All)
        {
            var profileNames = GetProfileSubKeys(descriptor.RegistryBasePath);

            if (profileNames.Count == 0)
            {
                // 占位条目：本机未安装该 CAD 版本，UI 中需展示但禁用
                results.Add(new CadInstallation(
                    descriptor,
                    ProfileSubKeys:   [],
                    IsCadInstalled:   false,
                    Status:           PluginDeployStatus.NotInstalled,
                    InstalledVersion: null,
                    InstalledBuildId: null,
                    InstalledDllPath: null));
                continue;
            }

            results.Add(ReadInstallation(descriptor, profileNames));
        }

        return results;
    }

    /// <summary>
    /// 读取同一 CAD 版本下所有配置文件实例的聚合插件状态。
    /// </summary>
    private static CadInstallation ReadInstallation(CadDescriptor descriptor, List<string> profileSubKeys)
    {
        var statuses          = new List<PluginDeployStatus>(profileSubKeys.Count);
        string? firstVersion  = null;
        string? firstBuildId  = null;
        string? firstDllPath  = null;

        foreach (var profileSubKey in profileSubKeys)
        {
            var appPath = $@"{descriptor.RegistryBasePath}\{profileSubKey}\Applications\{descriptor.AppName}";
            using var appKey = Registry.CurrentUser.OpenSubKey(appPath, false);

            var installedVersion = appKey?.GetValue("PluginVersion") as string;
            var installedBuildId = appKey?.GetValue("PluginBuildId") as string;
            var dllPath          = appKey?.GetValue("LOADER") as string;
            var status           = StatusResolver.Resolve(appKey is not null, dllPath, installedVersion, installedBuildId);

            statuses.Add(status);

            firstVersion ??= installedVersion;
            firstBuildId ??= installedBuildId;
            firstDllPath ??= dllPath;
        }

        return new CadInstallation(
            descriptor,
            profileSubKeys,
            IsCadInstalled:   true,
            Status:           ResolveAggregateStatus(statuses),
            InstalledVersion: firstVersion,
            InstalledBuildId: firstBuildId,
            InstalledDllPath: firstDllPath);
    }

    /// <summary>
    /// 按“最需要用户处理”的优先级聚合同一 CAD 版本下多个配置的插件状态。
    /// </summary>
    private static PluginDeployStatus ResolveAggregateStatus(IReadOnlyCollection<PluginDeployStatus> statuses)
    {
        if (statuses.Contains(PluginDeployStatus.DllMissing))        return PluginDeployStatus.DllMissing;
        if (statuses.Contains(PluginDeployStatus.InstalledOutdated)) return PluginDeployStatus.InstalledOutdated;
        if (statuses.Contains(PluginDeployStatus.NotInstalled))      return PluginDeployStatus.NotInstalled;
        return PluginDeployStatus.InstalledCurrent;
    }

    /// <summary>
    /// 获取指定注册表基路径下所有匹配 AutoCAD 配置文件模式的子键名称。
    /// </summary>
    private static List<string> GetProfileSubKeys(string basePath)
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(basePath, false);
            if (baseKey is null) return [];

            return [.. baseKey.GetSubKeyNames()
                               .Where(name => ProfilePattern().IsMatch(name))
                               .OrderBy(name => name)];
        }
        catch
        {
            return [];
        }
    }
}
