namespace AFR.Constants;

/// <summary>
/// AFR 插件对外暴露的所有 CAD 命令名称常量。
/// <para>
/// 抽象目的：跨 CAD 品牌与版本统一命令字符串。命令实现专属于各品牌/版本，
/// 但命令名应保持一致，便于用户在不同 CAD 中获得一致的使用体验。
/// </para>
/// <para>
/// 使用方式：实现层在 <c>[CommandMethod(CommandNames.Xxx)]</c> 中引用本常量。
/// 由于均为 <c>const string</c>，可直接用于特性参数，零运行时成本。
/// </para>
/// <para>
/// 新增命令时：先在此处登记常量，再在对应实现层引用。DEBUG 诊断命令同样需要登记。
/// </para>
/// </summary>
internal static class CommandNames
{
    // ── Release 命令 ────────────────────────────────────────────────────
    /// <summary>主命令：弹出字体替换窗口。</summary>
    public const string Main = "AFR";
    /// <summary>查看插件日志。</summary>
    public const string Log = "AFRLOG";
    /// <summary>人工确认 DBText 单行文字修复标签。</summary>
    public const string DbTextLabel = "AFRDBTEXTLABEL";

    // ── 隐藏维护命令（不通过 CommandMethod 注册，不进入 CAD 补全/建议列表）────
    /// <summary>卸载插件并清理注册表。仅完整输入时由 UnknownCommand 路由触发。</summary>
    public const string Unload = "AFRUNLOAD";

    // ── DEBUG 诊断命令（仅在 DEBUG 构建中由实现层注册）─────────────────
    /// <summary>查看 DBText 修复模型状态与评估。</summary>
    public const string DbTextModel = "AFRDBTEXTMODEL";
    /// <summary>查看 MText / MLeader 格式与样式诊断。</summary>
    public const string ViewMText = "AFRVIEW";
    /// <summary>插入测试用 MText。</summary>
    public const string InsertMText = "AFRINSERT";
    /// <summary>转储当前 CAD 配置文件与注册表状态。</summary>
    public const string DumpProfile = "AFRDUMPPROFILE";
    /// <summary>列出候选 .aws 配置文件路径。</summary>
    public const string ShowAwsPath = "AFRSHOWAWSPATH";
    /// <summary>批量生成 DBText Big5 修复训练标签。</summary>
    public const string DbTextBatchTrain = "AFRDBTEXTBATCHTRAIN";
    /// <summary>检查单个文字对象和 DBText 模型候选。</summary>
    public const string InspectText = "AFRINSPECTTEXT";
    /// <summary>生成 PowerShell 反射探针脚本。</summary>
    public const string GenProbeScripts = "AFRGENPROBESCRIPTS";
    /// <summary>反射转储 CAD 对话框相关 API。</summary>
    public const string DumpDialogApi = "AFRDUMPDIALOGAPI";
}
