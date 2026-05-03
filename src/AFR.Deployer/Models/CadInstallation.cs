namespace AFR.Deployer.Models;

/// <summary>
/// 本机某个 CAD 配置文件实例的扫描结果。
/// <para>
/// 同一 CAD 版本（如 AutoCAD 2025）可能对应多个配置文件子键（如 ACAD-12345:409、ACAD-67890:804），
/// 每个子键在 UI 中单独成块展示。当本机未安装某受支持的 CAD 版本时，扫描器会生成
/// <see cref="ProfileSubKey"/> 为空字符串、<see cref="IsCadInstalled"/> 为 false 的占位条目，
/// 以便 UI 列出所有支持版本但禁用未安装项的操作。
/// </para>
/// </summary>
/// <param name="Descriptor">对应的静态版本描述符。</param>
/// <param name="ProfileSubKey">注册表中的配置文件子键名，如 "ACAD-12345:409"；占位条目为空字符串。</param>
/// <param name="IsCadInstalled">本机是否实际安装了该 CAD 版本（即注册表中存在对应配置文件子键）。</param>
/// <param name="Status">插件当前的部署状态。</param>
/// <param name="InstalledVersion">注册表中记录的已安装插件显示版本号，未安装时为 null。</param>
/// <param name="InstalledBuildId">注册表中记录的已安装插件构建标识，未安装时为 null。</param>
/// <param name="InstalledDllPath">注册表 LOADER 值记录的 DLL 物理路径，未安装时为 null。</param>
internal sealed record CadInstallation(
    CadDescriptor Descriptor,
    string ProfileSubKey,
    bool IsCadInstalled,
    PluginDeployStatus Status,
    string? InstalledVersion,
    string? InstalledBuildId,
    string? InstalledDllPath)
{
    /// <summary>该配置文件实例下插件的完整注册表路径；占位条目无意义但保持可计算。</summary>
    internal string RegistryAppPath =>
        $@"{Descriptor.RegistryBasePath}\{ProfileSubKey}\Applications\{Descriptor.AppName}";
}
