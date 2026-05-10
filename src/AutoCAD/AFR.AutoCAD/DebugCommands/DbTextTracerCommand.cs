#if DEBUG
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AFR.FontMapping;

[assembly: CommandClass(typeof(AFR.DebugCommands.DbTextTracerCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// DBText Hook 追踪器命令 - 查看和管理 Hook 追踪器。
/// </summary>
public sealed class DbTextTracerCommand
{
    [CommandMethod("AFRTRACERSTART", CommandFlags.Modal)]
    public static void StartTracer()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;

        ed.WriteMessage("\n启动 DBText Hook 追踪器...\n");
        DbTextHookTracer.Install();
        ed.WriteMessage("追踪器已启动。请执行以下操作以收集数据:\n");
        ed.WriteMessage("  1. 冷启动 AutoCAD 并打开包含 DBText 的目标 DWG\n");
        ed.WriteMessage("  2. 使用 AFRTRACERREPORT 或 AFRDBTEXTPROBE 查看真实 hook 统计\n");
        ed.WriteMessage("  3. 报告应包含 readString scope、TextEditor DBCS 解码与 code-page context 统计\n");
    }

    [CommandMethod("AFRTRACERSTOP", CommandFlags.Modal)]
    public static void StopTracer()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;

        ed.WriteMessage("\n停止 DBText Hook 追踪器...\n");
        DbTextHookTracer.Uninstall();
        ed.WriteMessage("追踪器已停止。\n");
    }

    [CommandMethod("AFRTRACERREPORT", CommandFlags.Modal)]
    public static void ShowReport()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;

        ed.WriteMessage("\n");
        string report = DbTextHookTracer.GetReport();
        ed.WriteMessage(report);
        ed.WriteMessage("\n");
    }
}
#endif
