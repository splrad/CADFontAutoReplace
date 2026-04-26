using System.Reflection;

namespace AFR.Deployer.Services;

/// <summary>
/// 提供部署工具自身的版本号，用于写入注册表 PluginVersion 值和比对已安装版本。
/// </summary>
internal static class DeployerVersionService
{
    private static string? _cachedVersion;

    /// <summary>
    /// 获取当前 EXE 的版本号字符串。
    /// <para>
    /// 优先读取 <see cref="AssemblyInformationalVersionAttribute"/>（与插件 DLL 保持一致），
    /// 回退到 <see cref="AssemblyName.Version"/>。
    /// </para>
    /// </summary>
    internal static string GetVersion()
    {
        if (_cachedVersion is not null) return _cachedVersion;

        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _cachedVersion = !string.IsNullOrWhiteSpace(informational)
            ? informational!
            : asm.GetName().Version?.ToString() ?? "0.0";

        return _cachedVersion;
    }
}
