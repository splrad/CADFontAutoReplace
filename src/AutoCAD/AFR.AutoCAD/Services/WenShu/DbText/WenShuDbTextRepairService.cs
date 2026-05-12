using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.WenShu.DbText;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Services.WenShu.DbText;

internal static class WenShuDbTextRepairService
{
    private static WenShuDbTextRepairRunSummary _lastRunSummary;

    public static WenShuDbTextRepairRunSummary LastRunSummary => _lastRunSummary;

    public static int Repair(Database db)
    {
        if (db == null)
            return 0;

        WenShuDbTextAdvisor? advisor = null;
        WenShuDrawingIdentity drawing = WenShuDrawingIdentity.FromDatabase(db);

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

                    WenShuDbTextContext context = WenShuDbTextEntitySnapshotBuilder.BuildContext(db, tr, dbText, drawing);
                    WenShuDbTextProblemDetection detection = WenShuDbTextProblemDetector.Detect(context);
                    if (!detection.HasProblem)
                        continue;

                    problems++;
                    IReadOnlyList<WenShuDbTextCandidate> generatedCandidates = detection.Candidates;
                    candidates += generatedCandidates.Count;

                    advisor ??= new WenShuDbTextAdvisor();
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

                    WenShuDbTextDecision decision = advisor.Evaluate(
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
        _lastRunSummary = new WenShuDbTextRepairRunSummary(scanned, problems, repaired, modelUnavailable);
        return repaired;
    }

    public static void WriteCommandLineSummary(WenShuDbTextRepairRunSummary summary)
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

    private static string Trim(string text)
    {
        return text.Length <= 60 ? text : text.Substring(0, 60) + "...";
    }
}

internal readonly record struct WenShuDbTextRepairRunSummary(int Scanned, int Problems, int Repaired, bool ModelUnavailable)
{
    public int Unrepaired => Math.Max(0, Problems - Repaired);
}

