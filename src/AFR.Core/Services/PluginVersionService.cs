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
    /// 获取当前插件 DLL 的完整版本号（形如 <c>9.0+20260503.1</c>）。
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

    /// <summary>
    /// 获取面向用户的显示版本号（形如 <c>9.0</c>），即 InformationalVersion 中 '+' 之前的部分。
    /// 用于日志头与 UI 展示，在迭代中不频繁变动。
    /// </summary>
    public static string GetDisplayVersion()
    {
        var full = GetPluginVersion();
        var idx = full.IndexOf('+');
        return idx > 0 ? full[..idx] : full;
    }

    /// <summary>
    /// 获取构建标识（形如 <c>20260503.1</c>）
    /// 同一显示版本下用于区分新旧构建；若未设置则返回空字符串。
    /// </summary>
    public static string GetBuildId()
    {
        var full = GetPluginVersion();
        var idx = full.IndexOf('+');
        return idx >= 0 && idx + 1 < full.Length ? full[(idx + 1)..] : string.Empty;
    }
}
