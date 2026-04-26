namespace AFR.Deployer.Models;

/// <summary>
/// 本机某个 CAD 配置文件实例的扫描结果。
/// <para>
/// 同一 CAD 版本（如 AutoCAD 2025）可能对应多个配置文件子键（如 ACAD-12345:409、ACAD-67890:804），
/// 每个子键在 UI 中单独成行展示。
/// </para>
/// </summary>
/// <param name="Descriptor">对应的静态版本描述符。</param>
/// <param name="ProfileSubKey">注册表中的配置文件子键名，如 "ACAD-12345:409"。</param>
/// <param name="Status">插件当前的部署状态。</param>
/// <param name="InstalledVersion">注册表中记录的已安装插件版本号，未安装时为 null。</param>
/// <param name="InstalledDllPath">注册表 LOADER 值记录的 DLL 物理路径，未安装时为 null。</param>
internal sealed record CadInstallation(
    CadDescriptor Descriptor,
    string ProfileSubKey,
    PluginDeployStatus Status,
    string? InstalledVersion,
    string? InstalledDllPath)
{
    /// <summary>该配置文件实例下插件的完整注册表路径。</summary>
    internal string RegistryAppPath =>
        $@"{Descriptor.RegistryBasePath}\{ProfileSubKey}\Applications\{Descriptor.AppName}";
}
