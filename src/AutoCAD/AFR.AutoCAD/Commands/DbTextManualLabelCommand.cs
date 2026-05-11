using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AFR.Constants;
using AFR.DbTextRepairModel;
using AFR.Platform;
using AFR.Services;
using AFR.Services.DbTextRepair;
using AFR.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Commands;

public sealed class DbTextManualLabelCommand
{
    [CommandMethod(CommandNames.DbTextLabel, CommandFlags.Modal)]
    public static void Execute()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        var ed = doc?.Editor;
        if (doc == null || ed == null)
            return;

        var options = new PromptNestedEntityOptions("\n选择要人工确认的单行文字对象: ");
        PromptNestedEntityResult result = ed.GetNestedEntity(options);
        if (result.Status != PromptStatus.OK)
            return;

        try
        {
            DbTextRepairModelStore.EnsureReady();
            if (!TryReadSelection(doc.Database, result.ObjectId, out DbTextManualSelection selection, out string error))
            {
                ed.WriteMessage($"\n{error}\n");
                return;
            }

            var window = new DbTextLabelWindow(new DbTextLabelDialogData
            {
                Metadata = selection.Metadata,
                CurrentText = selection.CurrentText,
                CandidateText = selection.CandidateText,
                Evidence = selection.Evidence
            });

            PlatformManager.Host.ShowModalWindow(window);
            if (window.DialogResult != true || window.SelectedAction == DbTextLabelDialogAction.None)
                return;

            string action = MapAction(window.SelectedAction);
            string selectedText = action == DbTextRepairModelConstants.ActionRepair
                ? window.SelectedText
                : selection.CurrentText;

            DbTextRepairModelRecord record;
            bool changed = false;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (tr.GetObject(selection.ObjectId, OpenMode.ForRead, false, true) is not DBText dbText)
                {
                    ed.WriteMessage("\n所选对象不再是 DBText。\n");
                    return;
                }

                string current = dbText.TextString ?? string.Empty;
                if (!string.Equals(current, selection.CurrentText, StringComparison.Ordinal))
                {
                    ed.WriteMessage("\n对象文本已变化，请重新运行命令确认。\n");
                    return;
                }

                record = BuildRecord(
                    doc.Database,
                    tr,
                    dbText,
                    selection.CandidateText,
                    selectedText,
                    action,
                    window.Note);

                if (action == DbTextRepairModelConstants.ActionRepair
                    && !string.Equals(current, selectedText, StringComparison.Ordinal))
                {
                    dbText.UpgradeOpen();
                    dbText.TextString = selectedText;
                    changed = true;
                }

                tr.Commit();
            }

            DbTextRepairModelStore.AppendLabel(record);
            if (changed)
                ed.Regen();

            ed.WriteMessage(
                action == DbTextRepairModelConstants.ActionRepair
                    ? $"\n已记录并写回: {selection.CurrentText} -> {selectedText}\n模型: {DbTextRepairModelStore.CanonicalPath}\n"
                    : $"\n已记录: {ActionDisplayName(action)}\n模型: {DbTextRepairModelStore.CanonicalPath}\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nDBText 人工确认失败: {ex.Message}\n");
            DiagnosticLogger.LogError("DBText 人工确认失败", ex);
        }
    }

    private static bool TryReadSelection(
        Database db,
        ObjectId objectId,
        out DbTextManualSelection selection,
        out string error)
    {
        selection = default;
        error = string.Empty;

        using var tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(objectId, OpenMode.ForRead, false, true) is not DBText dbText)
        {
            error = "只能选择 DBText 单行文字。";
            return false;
        }

        string current = dbText.TextString ?? string.Empty;
        DbTextRepairCandidateGenerator.TryGenerateBig5CarrierToGbkCandidate(
            current,
            out string candidate,
            out string candidateReason);

        string style = DescribeTextStyle(dbText.TextStyleId, tr);
        string ownerBlock = DescribeOwnerBlock(dbText, tr);
        selection = new DbTextManualSelection(
            objectId,
            dbText.Handle.ToString(),
            current,
            candidate,
            $"Handle={dbText.Handle}, Layer='{dbText.Layer}', Block='{ownerBlock}', Style={style}",
            $"Candidate={candidateReason}; Model={DbTextRepairModelStore.CanonicalPath}");
        tr.Commit();
        return true;
    }

    private static DbTextRepairModelRecord BuildRecord(
        Database db,
        Transaction tr,
        DBText dbText,
        string candidateText,
        string selectedText,
        string action,
        string note)
    {
        DbTextDrawingIdentity drawing = DbTextDrawingIdentity.FromDatabase(db);
        TextStyleIdentity style = GetTextStyleIdentity(tr, dbText);

        return new DbTextRepairModelRecord
        {
            RecordType = "label",
            SourceSetId = BuildSourceSetId(),
            TimestampUtc = DateTime.UtcNow.ToString("O"),
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
            CandidateText = candidateText,
            SelectedText = selectedText,
            Action = action,
            Note = note
        };
    }

    private static string BuildSourceSetId()
    {
        string machine = Environment.MachineName ?? "MACHINE";
        string user = Environment.UserName ?? "USER";
        string basis = machine + "\u001F" + user;
        return "manual-" + DbTextRepairModelJsonl.ComputeTextHash(basis).Substring(0, 10);
    }

    private static string MapAction(DbTextLabelDialogAction action)
    {
        return action switch
        {
            DbTextLabelDialogAction.Repair => DbTextRepairModelConstants.ActionRepair,
            DbTextLabelDialogAction.Keep => DbTextRepairModelConstants.ActionKeep,
            DbTextLabelDialogAction.GlyphIssue => DbTextRepairModelConstants.ActionGlyphIssue,
            _ => string.Empty
        };
    }

    private static string ActionDisplayName(string action)
    {
        return action switch
        {
            DbTextRepairModelConstants.ActionKeep => "保持当前",
            DbTextRepairModelConstants.ActionGlyphIssue => "字体问题",
            DbTextRepairModelConstants.ActionRepair => "写回正确文本",
            _ => action
        };
    }

    private static TextStyleIdentity GetTextStyleIdentity(Transaction tr, DBText dbText)
    {
        try
        {
            if (tr.GetObject(dbText.TextStyleId, OpenMode.ForRead, false, true) is TextStyleTableRecord style)
            {
                string typeFace = string.Empty;
                try
                {
                    typeFace = style.Font.TypeFace ?? string.Empty;
                }
                catch
                {
                    typeFace = string.Empty;
                }

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

    private static string DescribeTextStyle(ObjectId styleId, Transaction tr)
    {
        try
        {
            if (styleId.IsNull || styleId.IsErased)
                return "<null>";

            var style = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead, false, true);
            return $"Name='{style.Name}', FileName='{style.FileName}', BigFont='{style.BigFontFileName}', TypeFace='{style.Font.TypeFace}'";
        }
        catch (System.Exception ex)
        {
            return $"<unavailable {ex.GetType().Name}: {ex.Message}>";
        }
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

    private readonly record struct TextStyleIdentity(
        string Name,
        string FileName,
        string BigFontFileName,
        string TypeFace);

    private readonly record struct DbTextManualSelection(
        ObjectId ObjectId,
        string Handle,
        string CurrentText,
        string CandidateText,
        string Metadata,
        string Evidence);
}
