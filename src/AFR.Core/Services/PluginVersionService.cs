using System.Reflection;

namespace AFR.Services;

/// <summary>
/// 提供插件版本与配置架构版本信息。
/// <para>
/// 该服务用于注册表初始化阶段判断 DLL 是否已升级，以及默认配置是否需要迁移。
/// </para>
/// </summary>
public static class PluginVersionService
{
    /// <summary>当前注册表配置架构版本。</summary>
    public const int ConfigSchemaVersion = 2;

    /// <summary>
    /// 获取当前插件 DLL 的版本号。
    /// <para>
    /// 优先使用程序集信息版本，未设置时回退到程序集版本。
    /// </para>
    /// </summary>
    public static string GetPluginVersion()
    {
        var assembly = typeof(PluginVersionService).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion!;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }
}
