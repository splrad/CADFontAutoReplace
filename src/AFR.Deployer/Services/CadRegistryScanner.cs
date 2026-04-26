using System.IO;
using System.Text.RegularExpressions;
using AFR.Deployer.Models;
using Microsoft.Win32;

namespace AFR.Deployer.Services;

/// <summary>
/// 扫描本机注册表，枚举所有已安装的受支持 CAD 版本及各配置文件实例中插件的部署状态。
/// <para>
/// 每次调用 <see cref="Scan"/> 都会重新读取注册表，确保反映用户在工具运行期间的手动修改。
/// </para>
/// </summary>
internal static class CadRegistryScanner
{
    /// <summary>AutoCAD 配置文件子键的匹配模式，对所有 AutoCAD 版本通用。</summary>
    private static readonly Regex ProfilePattern =
        new(@"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$", RegexOptions.Compiled);

    /// <summary>
    /// 扫描注册表，返回所有本机存在的 CAD 配置文件实例列表（按品牌 → 版本 → 配置文件排序）。
    /// <para>
    /// 仅返回注册表基路径实际存在的版本；若某版本未安装则不出现在结果中。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<CadInstallation> Scan()
    {
        var results = new List<CadInstallation>();

        foreach (var descriptor in CadDescriptors.All)
        {
            var profileNames = GetProfileSubKeys(descriptor.RegistryBasePath);
            foreach (var profile in profileNames)
            {
                var appPath = $@"{descriptor.RegistryBasePath}\{profile}\Applications\{descriptor.AppName}";
                var installation = ReadInstallation(descriptor, profile, appPath);
                results.Add(installation);
            }
        }

        return results;
    }

    /// <summary>
    /// 读取单个配置文件实例下的插件状态。
    /// </summary>
    private static CadInstallation ReadInstallation(
        CadDescriptor descriptor, string profileSubKey, string appPath)
    {
        using var appKey = Registry.CurrentUser.OpenSubKey(appPath, false);

        if (appKey is null)
        {
            return new CadInstallation(descriptor, profileSubKey, PluginDeployStatus.NotInstalled, null, null);
        }

        var installedVersion = appKey.GetValue("PluginVersion") as string;
        var dllPath = appKey.GetValue("LOADER") as string;

        // DLL 路径记录在注册表中，但物理文件已不存在
        if (!string.IsNullOrEmpty(dllPath) && !File.Exists(dllPath))
        {
            return new CadInstallation(descriptor, profileSubKey, PluginDeployStatus.DllMissing, installedVersion, dllPath);
        }

        var currentVersion = DeployerVersionService.GetVersion();
        var status = string.Equals(installedVersion, currentVersion, StringComparison.OrdinalIgnoreCase)
            ? PluginDeployStatus.InstalledCurrent
            : PluginDeployStatus.InstalledOutdated;

        return new CadInstallation(descriptor, profileSubKey, status, installedVersion, dllPath);
    }

    /// <summary>
    /// 获取指定注册表基路径下所有匹配 AutoCAD 配置文件模式的子键名称。
    /// </summary>
    private static IEnumerable<string> GetProfileSubKeys(string basePath)
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(basePath, false);
            if (baseKey is null) return [];

            return baseKey.GetSubKeyNames()
                          .Where(name => ProfilePattern.IsMatch(name))
                          .OrderBy(name => name)
                          .ToList();
        }
        catch
        {
            return [];
        }
    }
}
