#if DEBUG
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AFR.FontMapping;

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
        ed.WriteMessage("  1. 打开包含 DBText 的 Big5 图纸\n");
        ed.WriteMessage("  2. 或使用 AFRDBTEXTPROBE 命令创建测试 DBText\n");
        ed.WriteMessage("  3. 然后使用 AFRTRACERREPORT 查看追踪报告\n");
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
