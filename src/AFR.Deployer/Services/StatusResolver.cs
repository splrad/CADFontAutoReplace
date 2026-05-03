using System.IO;
using AFR.Deployer.Models;

namespace AFR.Deployer.Services;

/// <summary>
/// 根据注册表原始数据判定插件的部署状态。
/// <para>
/// 将版本比对逻辑从 <see cref="CadRegistryScanner"/> 中解耦，使扫描器只负责读取原始数据。
/// </para>
/// </summary>
internal static class StatusResolver
{
    /// <summary>
    /// 根据注册表读取结果判定 <see cref="PluginDeployStatus"/>。
    /// <para>
    /// 版本一致性同时比对显示版本号（<c>PluginVersion</c>）与构建标识（<c>PluginBuildId</c>），
    /// 两者均匹配才视为最新；任一不同视为旧版本。
    /// </para>
    /// </summary>
    /// <param name="appKeyExists">注册表 Applications\AppName 键是否存在。</param>
    /// <param name="dllPath">注册表 LOADER 值记录的 DLL 路径，键不存在时为 null。</param>
    /// <param name="installedVersion">注册表 PluginVersion 值，键不存在时为 null。</param>
    /// <param name="installedBuildId">注册表 PluginBuildId 值，键不存在时为 null。</param>
    internal static PluginDeployStatus Resolve(
        bool appKeyExists,
        string? dllPath,
        string? installedVersion,
        string? installedBuildId)
    {
        if (!appKeyExists)
            return PluginDeployStatus.NotInstalled;

        if (!string.IsNullOrEmpty(dllPath) && !File.Exists(dllPath))
            return PluginDeployStatus.DllMissing;

        var currentVersion = DeployerVersionService.GetDisplayVersion();
        var currentBuildId = DeployerVersionService.GetBuildId();

        var versionMatch = string.Equals(installedVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        var buildMatch   = string.Equals(installedBuildId ?? string.Empty, currentBuildId, StringComparison.OrdinalIgnoreCase);

        return versionMatch && buildMatch
            ? PluginDeployStatus.InstalledCurrent
            : PluginDeployStatus.InstalledOutdated;
    }
}
