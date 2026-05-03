using System.Reflection;

namespace AFR.Deployer.Services;

/// <summary>
/// 提供部署工具自身的版本号，用于写入注册表 PluginVersion / PluginBuildId 值和比对已安装版本。
/// <para>
/// AssemblyInformationalVersion 形如 <c>9.0+20260503.1</c>：
/// <list type="bullet">
///   <item><see cref="GetDisplayVersion"/>: '+' 之前部分（X.X），用于 UI 与日志头展示。</item>
///   <item><see cref="GetBuildId"/>: '+' 之后部分，用于在显示版本不变时区分新旧构建。</item>
/// </list>
/// </para>
/// </summary>
internal static class DeployerVersionService
{
    private static string? _cachedFull;

    private static string GetFullVersion()
    {
        if (_cachedFull is not null) return _cachedFull;
        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _cachedFull = !string.IsNullOrWhiteSpace(informational)
            ? informational!
            : asm.GetName().Version?.ToString() ?? "0.0";
        return _cachedFull;
    }

    /// <summary>显示版本号（X.X），写入注册表 <c>PluginVersion</c> 值。</summary>
    internal static string GetDisplayVersion()
    {
        var full = GetFullVersion();
        var idx = full.IndexOf('+');
        return idx > 0 ? full[..idx] : full;
    }

    /// <summary>构建标识（'+' 之后），写入注册表 <c>PluginBuildId</c> 值；未设置时为空字符串。</summary>
    internal static string GetBuildId()
    {
        var full = GetFullVersion();
        var idx = full.IndexOf('+');
        return idx >= 0 && idx + 1 < full.Length ? full[(idx + 1)..] : string.Empty;
    }
}
