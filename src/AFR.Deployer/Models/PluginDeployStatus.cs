namespace AFR.Deployer.Models;

/// <summary>
/// 插件在某个 CAD 配置文件实例中的部署状态。
/// </summary>
public enum PluginDeployStatus
{
    /// <summary>注册表中不存在该插件的自动加载条目。</summary>
    NotInstalled,

    /// <summary>插件已安装，且版本与 EXE 内嵌版本一致（最新）。</summary>
    InstalledCurrent,

    /// <summary>插件已安装，但版本低于 EXE 内嵌版本（需更新）。</summary>
    InstalledOutdated,

    /// <summary>注册表条目存在，但 LOADER 指向的 DLL 文件已被移动或删除。</summary>
    DllMissing,
}
