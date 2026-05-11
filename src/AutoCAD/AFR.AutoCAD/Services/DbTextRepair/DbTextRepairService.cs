using System;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.DbTextRepairModel;

namespace AFR.Services.DbTextRepair;

internal static class DbTextRepairService
{
    public static int Repair(Database db)
    {
        if (db == null)
            return 0;

        DbTextRepairModelIndex index = DbTextRepairModelStore.LoadIndex(out DbTextRepairModelMergeReport modelReport);
        var advisor = new DbTextRepairAdvisor(index);
        DbTextDrawingIdentity drawing = DbTextDrawingIdentity.FromDatabase(db);

        int scanned = 0;
        int candidates = 0;
        int repaired = 0;
        int blocked = 0;
        int errors = 0;

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId blockId in blockTable)
        {
            BlockTableRecord block;
            try
            {
                block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                if (block.IsFromExternalReference || block.IsDependent)
                    continue;
            }
            catch
            {
                errors++;
                continue;
            }

            foreach (ObjectId entityId in block)
            {
                try
                {
                    if (tr.GetObject(entityId, OpenMode.ForRead, false, true) is not DBText dbText)
                        continue;

                    scanned++;
                    string current = dbText.TextString ?? string.Empty;
                    if (string.IsNullOrEmpty(current))
                        continue;

                    DbTextRepairCandidateGenerator.TryGenerateBig5CarrierToGbkCandidate(
                        current,
                        out string candidate,
                        out _);
                    if (!string.IsNullOrEmpty(candidate))
                        candidates++;

                    DbTextRepairDecision decision = advisor.Evaluate(
                        drawing,
                        dbText.Handle.ToString(),
                        current,
                        candidate);

                    if (decision.IsBlocked)
                    {
                        blocked++;
                        continue;
                    }

                    if (!decision.ShouldRepair)
                        continue;

                    dbText.UpgradeOpen();
                    dbText.TextString = decision.SelectedText;
                    repaired++;
                    DiagnosticLogger.Log(
                        "DBText模型修复",
                        $"Handle={dbText.Handle}: '{Trim(current)}' -> '{Trim(decision.SelectedText)}', Reason={decision.Reason}");
                }
                catch (Exception ex)
                {
                    errors++;
                    DiagnosticLogger.Log("DBText模型修复", $"对象处理失败: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        tr.Commit();
        DiagnosticLogger.Log(
            "DBText模型修复",
            $"扫描={scanned}, 候选={candidates}, 标签={index.LabelCount}, 冲突={index.ConflictCount}, " +
            $"阻塞={blocked}, 实际修复={repaired}, 错误={errors}, 模型={modelReport.ToSummary()}");
        return repaired;
    }

    private static string Trim(string text)
    {
        return text.Length <= 60 ? text : text.Substring(0, 60) + "...";
    }
}
