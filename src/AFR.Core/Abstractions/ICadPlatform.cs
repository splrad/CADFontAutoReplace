namespace AFR.Abstractions;

/// <summary>
/// CAD 平台的身份标识与版本特定常量接口。
/// <para>
/// 每个 CAD 版本的适配壳（如 AFR-ACAD2026）提供唯一实现，
/// 用于向核心逻辑提供平台名称、注册表路径、目标 DLL 等版本相关信息。
/// </para>
/// </summary>
public interface ICadPlatform
{
    // ── 身份标识：用于日志、UI 显示和区分不同 CAD 品牌/版本 ──

    /// <summary>CAD 品牌名称（如 "AutoCAD"、"中望CAD"）。</summary>
    string BrandName { get; }
    /// <summary>CAD 版本名称（如 "2026"）。</summary>
    string VersionName { get; }
    /// <summary>CAD 应用程序内部名称，用于注册表查找等。</summary>
    string AppName { get; }
    /// <summary>用于 UI 显示的完整名称（如 "AutoCAD 2026"）。</summary>
    string DisplayName { get; }

    // ── 注册表：用于读取 CAD 搜索路径等配置 ──

    /// <summary>注册表根路径（如 "SOFTWARE\\Autodesk\\AutoCAD"）。</summary>
    string RegistryBasePath { get; }
    /// <summary>注册表键名匹配模式，用于定位具体版本的注册表项。</summary>
    string RegistryKeyPattern { get; }

    // ── Hook 参数：用于安装原生字体 Hook ──

    /// <summary>目标 DLL 名称（如 "acdb26.dll"），Hook 需要定位该 DLL 中的函数。</summary>
    string AcDbDllName { get; }

    // ── 能力标记：标识平台是否支持某些特性 ──

    /// <summary>是否支持样式表与 MText 原生字体 Hook。</summary>
    bool SupportsNativeFontHooks { get; }
}
