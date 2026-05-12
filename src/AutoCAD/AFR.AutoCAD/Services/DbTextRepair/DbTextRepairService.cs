using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.DbTextAI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Services.DbTextRepair;

internal static class DbTextRepairService
{
    private static DbTextRepairRunSummary _lastRunSummary;

    public static DbTextRepairRunSummary LastRunSummary => _lastRunSummary;

    public static int Repair(Database db)
    {
        if (db == null)
            return 0;

        DbTextRepairAdvisor? advisor = null;
        DbTextDrawingIdentity drawing = DbTextDrawingIdentity.FromDatabase(db);

        int scanned = 0;
        int candidates = 0;
        int aiScored = 0;
        int problems = 0;
        int repaired = 0;
        int blocked = 0;
        int errors = 0;
        bool modelUnavailable = false;
        string aiStatus = "not-invoked";

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

                    DbTextAiContext context = BuildContext(db, tr, dbText, drawing);
                    DbTextAiProblemDetection detection = DbTextAiProblemDetector.Detect(context);
                    if (!detection.HasProblem)
                        continue;

                    problems++;
                    IReadOnlyList<DbTextAiCandidate> generatedCandidates = detection.Candidates;
                    candidates += generatedCandidates.Count;

                    advisor ??= new DbTextRepairAdvisor();
                    aiStatus = advisor.AiStatus;
                    if (!advisor.IsAiAvailable)
                    {
                        modelUnavailable = true;
                        blocked++;
                        DiagnosticLogger.Log(
                            "DBText文枢",
                            $"Handle={dbText.Handle}: 疑似异常={detection.Reason}, 文枢模型不可用, AI状态={advisor.AiStatus}");
                        continue;
                    }

                    DbTextAiDecision decision = advisor.Evaluate(
                        context,
                        generatedCandidates);
                    if (generatedCandidates.Any(c => c.HasAiScore))
                        aiScored++;

                    if (decision.IsBlocked)
                    {
                        blocked++;
                        DiagnosticLogger.Log(
                            "DBText文枢",
                            $"Handle={dbText.Handle}: 疑似异常={detection.Reason}, 安全阻断={decision.Reason}, AI={decision.AiSummary}");
                        continue;
                    }

                    if (!decision.ShouldRepair)
                    {
                        blocked++;
                        DiagnosticLogger.Log(
                            "DBText文枢",
                            $"Handle={dbText.Handle}: 疑似异常={detection.Reason}, 未写回={decision.Reason}, AI={decision.AiSummary}");
                        continue;
                    }

                    dbText.UpgradeOpen();
                    dbText.TextString = decision.SelectedText;
                    repaired++;
                    DiagnosticLogger.Log(
                        "DBText文枢",
                        $"Handle={dbText.Handle}: '{Trim(current)}' -> '{Trim(decision.SelectedText)}', 疑似异常={detection.Reason}, Reason={decision.Reason}, AI={decision.AiSummary}");
                }
                catch (Exception ex)
                {
                    errors++;
                    DiagnosticLogger.Log("DBText文枢", $"对象处理失败: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        tr.Commit();
        DiagnosticLogger.Log(
            "DBText文枢",
            $"扫描={scanned}, 疑似异常={problems}, 候选={candidates}, AI评分={aiScored}, AI状态={aiStatus}, " +
            $"阻塞={blocked}, 实际修复={repaired}, 错误={errors}");
        _lastRunSummary = new DbTextRepairRunSummary(scanned, problems, repaired, modelUnavailable);
        return repaired;
    }

    public static void WriteCommandLineSummary(DbTextRepairRunSummary summary)
    {
        if (summary.Problems <= 0)
            return;

        var editor = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (editor == null)
            return;

        string message;
        if (summary.ModelUnavailable)
            message = "[AFR 文枢] 当前 文枢 决策模型不可用；未执行 DBText 自动修复。";
        else if (summary.Repaired > 0)
            message = $"[AFR 文枢] 检测到疑似 DBText 乱码；文枢 已通过安全校验并成功修复 {summary.Repaired} 项。";
        else
            message = "[AFR 文枢] 检测到疑似 DBText 异常；当前候选结果未通过 文枢 安全校验，未执行写回。";

        string summaryLine = $"扫描={summary.Scanned}, 疑似异常={summary.Problems}, 修复={summary.Repaired}, 未修复={summary.Unrepaired}";

        DiagnosticLogger.Log("DBText文枢", summaryLine);
        DiagnosticLogger.Log("DBText文枢", message);
        DiagnosticLogger.Flush();

        editor.WriteMessage($"\n{message}\n");
    }

    private static DbTextAiContext BuildContext(
        Database db,
        Transaction tr,
        DBText dbText,
        DbTextDrawingIdentity drawing)
    {
        TextStyleIdentity style = GetTextStyleIdentity(tr, dbText);
        return new DbTextAiContext
        {
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
            CurrentText = dbText.TextString ?? string.Empty,
            IsFromExternalReference = IsFromExternalReference(dbText, tr)
        };
    }

    private static bool IsFromExternalReference(DBText dbText, Transaction tr)
    {
        try
        {
            if (tr.GetObject(dbText.OwnerId, OpenMode.ForRead, false, true) is BlockTableRecord owner)
                return owner.IsFromExternalReference || owner.IsDependent;
        }
        catch
        {
            // ignored
        }

        return false;
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

internal readonly record struct DbTextRepairRunSummary(int Scanned, int Problems, int Repaired, bool ModelUnavailable)
{
    public int Unrepaired => Math.Max(0, Problems - Repaired);
}
