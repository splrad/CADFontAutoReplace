using Autodesk.AutoCAD.Runtime;
using AFR.Constants;
using AFR.Services.DbTextRepair;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Commands;

public sealed class DbTextModelCommand
{
    [CommandMethod(CommandNames.DbTextModel, CommandFlags.Modal)]
    public static void Execute()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null)
            return;

        var report = DbTextRepairModelStore.ForceMerge();
        var index = DbTextRepairModelStore.LoadIndex(out _);

        ed.WriteMessage("\n=== AFR DBText Repair Model ===\n");
        ed.WriteMessage($"ActiveDirectory: {DbTextRepairModelStore.ActiveDirectory}\n");
        ed.WriteMessage($"CanonicalPath: {DbTextRepairModelStore.CanonicalPath}\n");
        ed.WriteMessage($"Labels: {index.LabelCount}\n");
        ed.WriteMessage($"Conflicts: {index.ConflictCount}\n");
        ed.WriteMessage($"LastMerge: {report.ToSummary()}\n");
    }
}
