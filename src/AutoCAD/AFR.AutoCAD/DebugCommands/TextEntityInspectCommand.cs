#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AFR.Constants;
using AFR.DbTextRepairModel;
using AFR.Services;
using AFR.Services.DbTextRepair;

[assembly: CommandClass(typeof(AFR.DebugCommands.TextEntityInspectCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// DEBUG-only text entity inspector for the current DBText model repair path.
/// It intentionally avoids retired native hook/probe state.
/// </summary>
public sealed class TextEntityInspectCommand
{
    [CommandMethod(CommandNames.InspectText, CommandFlags.Modal)]
    public static void Execute()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc?.Editor;
        if (doc == null || ed == null)
            return;

        var options = new PromptEntityOptions("\n选择要检查的文字对象: ");
        PromptEntityResult result = ed.GetEntity(options);
        if (result.Status != PromptStatus.OK)
            return;

        DiagnosticLogger.BeginDocument(doc.Name, "", "", "");
        DiagnosticLogger.BeginPhase("检查文字对象");

        try
        {
            DbTextRepairModelStore.EnsureReady();
            DbTextRepairModelIndex index = DbTextRepairModelStore.LoadIndex(out DbTextRepairModelMergeReport modelReport);
            var advisor = new DbTextRepairAdvisor(index);

            using var tr = doc.Database.TransactionManager.StartTransaction();
            DBObject obj = tr.GetObject(result.ObjectId, OpenMode.ForRead, false, true);
            string report = BuildReport(doc.Database, obj, tr, index, advisor, modelReport);
            ed.WriteMessage("\n" + report + "\n");
            DiagnosticLogger.Log("TextInspect", report);
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n检查失败: {ex.GetType().Name}: {ex.Message}\n");
            DiagnosticLogger.LogError("TextInspect 执行失败", ex);
        }
        finally
        {
            DiagnosticLogger.EndPhase();
            DiagnosticLogger.WriteSummary();
            DiagnosticLogger.Flush();
        }
    }

    private static string BuildReport(
        Database db,
        DBObject obj,
        Transaction tr,
        DbTextRepairModelIndex index,
        DbTextRepairAdvisor advisor,
        DbTextRepairModelMergeReport modelReport)
    {
        var lines = new List<string>
        {
            "=== AFR Text Entity Inspect ===",
            $"ObjectId: {obj.ObjectId}",
            $"Handle: {Safe(() => obj.Handle.ToString())}",
            $".NET Type: {obj.GetType().FullName}",
            $"RXClass: {Safe(() => obj.GetRXClass()?.Name ?? "<null>")}",
            $"DxfName: {Safe(() => obj.GetRXClass()?.DxfName ?? "<null>")}"
        };

        if (obj is Entity entity)
        {
            lines.Add($"Layer: {Safe(() => entity.Layer)}");
            lines.Add($"ColorIndex: {Safe(() => entity.ColorIndex.ToString())}");
        }

        switch (obj)
        {
            case DBText dbText:
                AppendDbText(lines, db, tr, dbText, index, advisor, modelReport);
                break;
            case MText mText:
                lines.Add($"MText.Contents: {Escape(mText.Contents)}");
                lines.Add($"MText.Text: {Escape(mText.Text)}");
                lines.Add($"TextStyleId: {Safe(() => DescribeTextStyle(mText.TextStyleId, tr))}");
                break;
            case MLeader mLeader:
                lines.Add($"MLeader.ContentType: {mLeader.ContentType}");
                lines.Add($"MLeader.MText?.Contents: {Escape(mLeader.MText?.Contents ?? string.Empty)}");
                lines.Add($"MLeader.TextStyleId: {Safe(() => DescribeTextStyle(mLeader.TextStyleId, tr))}");
                break;
            default:
                lines.Add("TextInspect: unsupported-entity-type");
                break;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendDbText(
        List<string> lines,
        Database db,
        Transaction tr,
        DBText dbText,
        DbTextRepairModelIndex index,
        DbTextRepairAdvisor advisor,
        DbTextRepairModelMergeReport modelReport)
    {
        string current = dbText.TextString ?? string.Empty;
        DbTextDrawingIdentity drawing = DbTextDrawingIdentity.FromDatabase(db);
        TextStyleIdentity style = GetTextStyleIdentity(tr, dbText);
        var context = new DbTextRepairModelRecord
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
            Layer = Safe(() => dbText.Layer),
            OwnerBlockName = DescribeOwnerBlock(dbText, tr),
            TextStyleName = style.Name,
            TextStyleFileName = style.FileName,
            TextStyleBigFontFileName = style.BigFontFileName,
            TextStyleTypeFace = style.TypeFace,
            CurrentText = current
        };

        IReadOnlyList<DbTextRepairCandidate> candidates = DbTextRepairCandidateGenerator.BuildCandidates(current, index);
        DbTextRepairDecision decision = advisor.Evaluate(context, candidates);

        lines.Add($"DBText.TextString: {Escape(current)}");
        lines.Add($"TextStyleId: {Safe(() => DescribeTextStyle(dbText.TextStyleId, tr))}");
        lines.Add($"OwnerBlock: {Escape(context.OwnerBlockName)}");
        lines.Add($"ModelPath: {DbTextRepairModelStore.CanonicalPath}");
        lines.Add($"ModelMerge: {modelReport.ToSummary()}");
        lines.Add($"ModelLabels: {index.LabelCount}");
        lines.Add($"ModelConflicts: {index.ConflictCount}");
        lines.Add($"NeuralRanker: {advisor.NeuralRankerStatus}");
        lines.Add($"DecisionAction: {decision.Action}");
        lines.Add($"DecisionReason: {decision.Reason}");
        lines.Add($"DecisionSelectedText: {Escape(decision.SelectedText)}");
        if (!string.IsNullOrWhiteSpace(decision.NeuralSummary))
            lines.Add($"DecisionNeuralSummary: {Escape(decision.NeuralSummary)}");

        lines.Add($"CandidateCount: {candidates.Count}");
        int indexNumber = 0;
        foreach (DbTextRepairCandidate candidate in candidates.OrderByDescending(c => c.HasNeuralScore ? c.NeuralScore : -1))
        {
            string score = candidate.HasNeuralScore ? candidate.NeuralScore.ToString("0.000") : "<none>";
            lines.Add(
                $"Candidate[{indexNumber}]: Text='{Escape(candidate.Text)}', Source='{Escape(candidate.Source)}', " +
                $"Reason='{Escape(candidate.Reason)}', AI={score}");
            indexNumber++;
        }
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
            // Diagnostic-only command; missing style data is non-fatal.
        }

        return new TextStyleIdentity(string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static string DescribeTextStyle(ObjectId styleId, Transaction tr)
    {
        if (styleId.IsNull || styleId.IsErased)
            return "<null>";

        var style = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead, false, true);
        return $"Name='{style.Name}', FileName='{style.FileName}', BigFont='{style.BigFontFileName}', TypeFace='{style.Font.TypeFace}'";
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
            // Diagnostic-only command.
        }

        return string.Empty;
    }

    private static string SafeObjectId(ObjectId id)
    {
        try { return id.ToString(); }
        catch { return string.Empty; }
    }

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (System.Exception ex) { return "<读取失败:" + ex.GetType().Name + ":" + ex.Message + ">"; }
    }

    private static string Escape(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private readonly record struct TextStyleIdentity(
        string Name,
        string FileName,
        string BigFontFileName,
        string TypeFace);
}
#endif
