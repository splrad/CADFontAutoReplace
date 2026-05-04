namespace AFR.Deployer.Models;

/// <summary>
/// 本机某个 CAD 版本的扫描结果。
/// <para>
/// 同一 CAD 版本（如 AutoCAD 2025）可能对应多个配置文件子键（如 ACAD-12345:409、ACAD-67890:804），
/// UI 仅按 CAD 版本展示一张卡片，安装 / 卸载时覆盖该版本下的全部配置文件子键。
/// 当本机未安装某受支持的 CAD 版本时，扫描器会生成 <see cref="ProfileSubKeys"/> 为空、
/// <see cref="IsCadInstalled"/> 为 false 的占位条目，以便 UI 列出所有支持版本但禁用未安装项的操作。
/// </para>
/// </summary>
/// <param name="Descriptor">对应的静态版本描述符。</param>
/// <param name="ProfileSubKeys">注册表中的配置文件子键名集合，如 "ACAD-12345:409"；占位条目为空集合。</param>
/// <param name="IsCadInstalled">本机是否实际安装了该 CAD 版本。</param>
/// <param name="Status">插件当前的部署状态。</param>
/// <param name="InstalledVersion">注册表中记录的已安装插件显示版本号，未安装时为 null。</param>
/// <param name="InstalledBuildId">注册表中记录的已安装插件构建标识，未安装时为 null。</param>
/// <param name="InstalledDllPath">注册表 LOADER 值记录的 DLL 物理路径，未安装时为 null。</param>
internal sealed record CadInstallation(
    CadDescriptor Descriptor,
    IReadOnlyList<string> ProfileSubKeys,
    bool IsCadInstalled,
    PluginDeployStatus Status,
    string? InstalledVersion,
    string? InstalledBuildId,
    string? InstalledDllPath)
{
    /// <summary>用于 UI 辅助显示的配置文件摘要；占位条目显示为空字符串。</summary>
    internal string ProfileSubKey => ProfileSubKeys.Count switch
    {
        0 => string.Empty,
        1 => ProfileSubKeys[0],
        _ => $"{ProfileSubKeys.Count} 个配置",
    };

    /// <summary>获取指定配置文件子键下插件的完整注册表路径。</summary>
    internal string GetRegistryAppPath(string profileSubKey) =>
        $@"{Descriptor.RegistryBasePath}\{profileSubKey}\Applications\{Descriptor.AppName}";

    /// <summary>获取指定配置文件子键的根注册表路径。</summary>
    internal string GetProfileRootPath(string profileSubKey) =>
        $@"{Descriptor.RegistryBasePath}\{profileSubKey}";
}
