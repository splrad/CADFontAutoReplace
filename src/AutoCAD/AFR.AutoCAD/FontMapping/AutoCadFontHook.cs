using AFR.Abstractions;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// AutoCAD 平台的 <see cref="IFontHook"/> 实现。
/// 本身不包含 Hook 逻辑，仅作为接口适配器安装全局字体加载 Hook。
/// </summary>
internal sealed class AutoCadFontHook : IFontHook
{
    /// <summary>Hook 是否已安装并处于拦截状态。</summary>
    public bool IsInstalled =>
        LdFileHook.IsInstalled
        || ShpLoadHook.IsInstalled
        || StyleTextStyleHook.IsInstalled
        || MTextInlineFontHook.IsInstalled
#if DEBUG
        || MapFontDiagnosticHook.IsInstalled
#endif
        ;

    /// <summary>安装插件级持久字体 Hook，并初始化 CAD 字体兜底索引。</summary>
    public void Install()
    {
        FontAvailabilityIndex.Initialize();
#if DEBUG
        MapFontDiagnosticHook.Install();
#endif
        LdFileHook.Install();
        ShpLoadHook.Install();
        StyleTextStyleHook.Install();
        MTextInlineFontHook.Install();
    }

    /// <summary>卸载 Hook，恢复被拦截的 AcGiTextStyle 函数。</summary>
    public void Uninstall()
    {
        MTextInlineFontHook.Uninstall();
        StyleTextStyleHook.Uninstall();
        ShpLoadHook.Uninstall();
        LdFileHook.Uninstall();
#if DEBUG
        MapFontDiagnosticHook.Uninstall();
#endif
    }

    /// <summary>更新 Hook 使用的替换字体配置（用户通过 AFR 命令修改后调用）。</summary>
    public void UpdateConfig()
    {
        FontAvailabilityIndex.Initialize();
    }
}
