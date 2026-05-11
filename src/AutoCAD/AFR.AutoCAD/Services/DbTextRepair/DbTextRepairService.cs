using System;
using System.Collections.Generic;
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
        int aiScored = 0;
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

                    DbTextRepairModelRecord context = BuildContext(db, tr, dbText, drawing);
                    IReadOnlyList<DbTextRepairCandidate> generatedCandidates =
                        DbTextRepairCandidateGenerator.BuildCandidates(current, index);
                    candidates += generatedCandidates.Count;

                    DbTextRepairDecision decision = advisor.Evaluate(
                        context,
                        generatedCandidates);
                    if (generatedCandidates.Count > 0 && generatedCandidates[0].HasNeuralScore)
                        aiScored++;

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
                        $"Handle={dbText.Handle}: '{Trim(current)}' -> '{Trim(decision.SelectedText)}', Reason={decision.Reason}, AI={decision.NeuralSummary}");
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
            $"扫描={scanned}, 候选={candidates}, AI评分={aiScored}, AI状态={advisor.NeuralRankerStatus}, " +
            $"标签={index.LabelCount}, 冲突={index.ConflictCount}, 阻塞={blocked}, 实际修复={repaired}, " +
            $"错误={errors}, 模型={modelReport.ToSummary()}");
        return repaired;
    }

    private static DbTextRepairModelRecord BuildContext(
        Database db,
        Transaction tr,
        DBText dbText,
        DbTextDrawingIdentity drawing)
    {
        TextStyleIdentity style = GetTextStyleIdentity(tr, dbText);
        return new DbTextRepairModelRecord
        {
            RecordType = DbTextRepairModelConstants.RecordTypeLabel,
            DrawingPath = drawing.Path,
            DrawingFileName = drawing.FileName,
            DrawingLength = drawing.Length,
            DrawingLastWriteUtc = drawing.LastWriteUtc,
            DrawingSha256 = drawing.Sha256,
            EntityType = "DBText",
            ObjectId = SafeObjectId(dbText.ObjectId),
            Handle = dbText.Handle.ToString(),
            Layer = Safe(() => dbText.Layer, string.Empty),
            OwnerBlockName = DescribeOwnerBlock(dbText, tr),
            TextStyleName = style.Name,
            TextStyleFileName = style.FileName,
            TextStyleBigFontFileName = style.BigFontFileName,
            TextStyleTypeFace = style.TypeFace,
            CurrentText = dbText.TextString ?? string.Empty
        };
    }

    private static TextStyleIdentity GetTextStyleIdentity(Transaction tr, DBText dbText)
    {
        try
        {
            if (tr.GetObject(dbText.TextStyleId, OpenMode.ForRead, false, true) is TextStyleTableRecord style)
            {
                string typeFace = string.Empty;
                try { typeFace = style.Font.TypeFace ?? string.Empty; }
                catch { typeFace = string.Empty; }

                return new TextStyleIdentity(
                    style.Name,
                    style.FileName ?? string.Empty,
                    style.BigFontFileName ?? string.Empty,
                    typeFace);
            }
        }
        catch
        {
            // ignored
        }

        return new TextStyleIdentity(string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static string DescribeOwnerBlock(DBText dbText, Transaction tr)
    {
        try
        {
            if (tr.GetObject(dbText.OwnerId, OpenMode.ForRead, false, true) is BlockTableRecord owner)
                return owner.Name;
        }
        catch
        {
            // ignored
        }

        return string.Empty;
    }

    private static string SafeObjectId(ObjectId id)
    {
        try { return id.ToString(); }
        catch { return string.Empty; }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static string Trim(string text)
    {
        return text.Length <= 60 ? text : text.Substring(0, 60) + "...";
    }

    private readonly record struct TextStyleIdentity(
        string Name,
        string FileName,
        string BigFontFileName,
        string TypeFace);
}
