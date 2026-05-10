#if DEBUG
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DBText code page 追踪报告器。
/// <para>
/// 该类型不再维护候选 RVA 的假命中计数；实际证据来自
/// <see cref="DwgFilerCodePageScopeHook"/>、<see cref="DbTextDwgInFieldsScopeHook"/>、
/// <see cref="TextEditorDbcsDecodeHook"/> 与 <see cref="CodePageFamilyHook"/> 的运行时统计。
/// </para>
/// </summary>
internal static class DbTextHookTracer
{
    private static bool _enabled;

    /// <summary>当前是否处于只读追踪模式。</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>启用追踪报告。</summary>
    public static void Install()
    {
        _enabled = true;
        DiagnosticLogger.Log("DbTextTracer", "已启用。请打开目标 DWG 后查看真实 hook 统计。");
    }

    /// <summary>停用追踪报告。</summary>
    public static void Uninstall()
    {
        _enabled = false;
        DiagnosticLogger.Log("DbTextTracer", "已停用。");
    }

    /// <summary>获取真实 hook 统计报告。</summary>
    public static string GetReport()
    {
        return string.Join(Environment.NewLine,
            "=== DBText Code Page 追踪报告 ===",
            $"Enabled: {_enabled}",
            "说明: 当前报告来自实际 readString/code-page context hook，不再显示未安装探针的候选 RVA 假命中计数。",
            "",
            DwgFilerCodePageScopeHook.GetReport(),
            "",
            DbTextDwgInFieldsScopeHook.GetReport(),
            "",
            TextEditorDbcsDecodeHook.GetReport(),
            "",
            CodePageFamilyHook.GetReport());
    }
}
#endif
