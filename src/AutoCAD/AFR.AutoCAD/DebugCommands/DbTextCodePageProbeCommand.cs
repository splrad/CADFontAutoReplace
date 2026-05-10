#if DEBUG
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AFR.FontMapping;
using AFR.Services;

[assembly: CommandClass(typeof(AFR.DebugCommands.DbTextCodePageProbeCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// DBText Code Page 探针命令。
/// <para>
/// 输出真实 readString 作用域、TextEditor DBCS 解码与 code-page context hook 统计，
/// 用于验证 DWG 加载期间是否根据 filer code page 修正了解码链路。
/// </para>
/// </summary>
public sealed class DbTextCodePageProbeCommand
{
    /// <summary>输出当前 DBCS code page hook 诊断报告。</summary>
    [CommandMethod("AFRDBTEXTPROBE", CommandFlags.Modal)]
    public static void Execute()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;

        DiagnosticLogger.BeginDocument("DBTextCodePageProbe", "", "", "");
        DiagnosticLogger.BeginPhase("输出 DBText Code Page Hook 诊断");

        try
        {
            ed.WriteMessage("\n=== DBText Code Page Hook 诊断 ===\n");
            ed.WriteMessage("请先冷启动 AutoCAD 并打开目标 DWG，再运行本命令查看 DWG 加载期间的真实 hook 统计。\n");
            ed.WriteMessage("本命令不再创建测试 DBText；新建对象不会触发 DWG readString 解码链路。\n\n");

            string report = DbTextHookTracer.GetReport();
            ed.WriteMessage(report);
            ed.WriteMessage("\n");
            DiagnosticLogger.Log("DbTextProbe", report);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n探针报告输出失败: {ex.Message}\n");
            DiagnosticLogger.LogError("DbTextProbe 执行失败", ex);
        }
        finally
        {
            DiagnosticLogger.EndPhase();
            DiagnosticLogger.WriteSummary();
        }
    }
}
#endif
