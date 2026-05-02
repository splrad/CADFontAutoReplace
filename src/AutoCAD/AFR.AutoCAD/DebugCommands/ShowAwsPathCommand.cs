#if DEBUG
using AFR.Diagnostics;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.DebugCommands.ShowAwsPathCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// AFRSHOWAWSPATH 命令（仅 DEBUG）：输出当前定位到的 <c>FixedProfile.aws</c> 候选/活动路径，
/// 并打印当前 HideableDialog 节点 XML（若存在）。
/// </summary>
public static class ShowAwsPathCommand
{
    /// <summary>命令入口。</summary>
    [CommandMethod("AFRSHOWAWSPATH")]
    public static void ShowAwsPath()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;
        var all = AwsHideableDialogPatcher.ListTargetAwsFiles();
        ed.WriteMessage($"\n[AFR] candidates ({all.Length}):\n");
        foreach (var c in all) ed.WriteMessage($"  - {c}\n");
        var p = AwsHideableDialogPatcher.LocateActiveAwsPath();
        ed.WriteMessage($"[AFR] active: {p ?? "(not found)"}\n");
        if (p != null)
        {
            var node = AwsHideableDialogPatcher.ReadDialogNodeXml(p);
            ed.WriteMessage(string.IsNullOrEmpty(node)
                ? "[AFR] HideableDialog node: (absent)\n"
                : $"[AFR] HideableDialog node:\n{node}\n");
        }
        if (all.Length == 0)
        {
            ed.WriteMessage("[AFR] HINT: FixedProfile.aws 不存在 → AutoCAD 尚未生成。\n");
            ed.WriteMessage("       OPTIONS → 配置 → 当前配置 → '另存为...' 任意名 → 关闭 OPTIONS → EXIT 退出 AutoCAD，文件即生成。\n");
        }
    }
}
#endif
