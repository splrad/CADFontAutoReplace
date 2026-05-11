#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AFR.Constants;
using AFR.DbTextRepairModel;
using AFR.Services;
using AFR.Services.DbTextRepair;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.DebugCommands.DbTextBatchTrainCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// 仅 DEBUG：保守批量生成 DBText Big5 乱码修复训练标签。
/// </summary>
public static class DbTextBatchTrainCommand
{
    private const string SourceSetId = "github-shared";
    private const string BatchNote = "batch-big5-conservative; no-drawing-write";

#if !NETFRAMEWORK
    private static int _providerRegistered;
#endif

    [CommandMethod(CommandNames.DbTextBatchTrain, CommandFlags.Modal)]
    public static void Execute()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        var ed = doc?.Editor;
        if (doc == null || ed == null)
            return;

        try
        {
            DbTextRepairModelStore.EnsureReady();
            DbTextRepairModelIndex index = DbTextRepairModelStore.LoadIndex(out DbTextRepairModelMergeReport modelReport);
            var advisor = new DbTextRepairAdvisor(index);
            BatchScanResult result = Scan(doc.Database, index, advisor, modelReport);

            if (result.Labels.Count > 0)
                DbTextRepairModelStore.AppendLabels(result.Labels);

            string trainingStatus = DbTextRepairModelStore.LastNeuralTrainingStatus;
            string reportPath = WriteAuditReport(result, trainingStatus);
            DiagnosticLogger.Log(
                "DBText批量训练",
                $"扫描={result.Scanned}, 写入={result.Labels.Count}, 跳过={result.Skipped}, 错误={result.Errors}, " +
                $"模型训练={trainingStatus}, 报告={reportPath}");
            DiagnosticLogger.Flush();

            ed.WriteMessage(
                $"\n[DBText批量训练] 扫描={result.Scanned}, 写入={result.Labels.Count}, 跳过={result.Skipped}, 错误={result.Errors}\n" +
                $"[DBText批量训练] 模型: {DbTextRepairModelStore.CanonicalPath}\n" +
                $"[DBText批量训练] 模型训练: {trainingStatus}\n" +
                $"[DBText批量训练] 报告: {reportPath}\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n[DBText批量训练] 失败: {ex.GetType().Name}: {ex.Message}\n");
            DiagnosticLogger.LogError("DBText批量训练失败", ex);
        }
    }

    private static BatchScanResult Scan(
        Database db,
        DbTextRepairModelIndex index,
        DbTextRepairAdvisor advisor,
        DbTextRepairModelMergeReport modelReport)
    {
        DbTextDrawingIdentity drawing = DbTextDrawingIdentity.FromDatabase(db);
        var result = new BatchScanResult(
            drawing,
            advisor.NeuralRankerStatus,
            DbTextRepairModelStore.CanonicalPath,
            modelReport.ToSummary());
        if (string.IsNullOrEmpty(drawing.Sha256))
        {
            result.Errors++;
            result.Rows.Add(BatchAuditRow.Error("drawing-sha256-missing", drawing.Path));
            return result;
        }

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
            catch (System.Exception ex)
            {
                result.Errors++;
                result.Rows.Add(BatchAuditRow.Error("read-block-failed", ex.Message));
                continue;
            }

            foreach (ObjectId entityId in block)
            {
                try
                {
                    if (tr.GetObject(entityId, OpenMode.ForRead, false, true) is not DBText dbText)
                        continue;

                    result.Scanned++;
                    DbTextRepairModelRecord context = BuildContext(db, tr, dbText, drawing);
                    ProcessDbText(context, index, advisor, result);
                }
                catch (System.Exception ex)
                {
                    result.Errors++;
                    result.Rows.Add(BatchAuditRow.Error("read-entity-failed", ex.Message));
                }
            }
        }

        tr.Commit();
        return result;
    }

    private static void ProcessDbText(
        DbTextRepairModelRecord context,
        DbTextRepairModelIndex index,
        DbTextRepairAdvisor advisor,
        BatchScanResult result)
    {
        string current = context.CurrentText ?? string.Empty;
        if (string.IsNullOrEmpty(current))
        {
            Skip(result, context, "empty-current");
            return;
        }

        if (!IsAllowedProblemStyle(context))
        {
            Skip(result, context, "unsupported-style");
            return;
        }

        IReadOnlyList<DbTextRepairCandidate> candidates = DbTextRepairCandidateGenerator.BuildCandidates(current, index);
        advisor.ScoreCandidates(context, candidates);

        DbTextRepairCandidate? currentCandidate = candidates.FirstOrDefault(
            c => string.Equals(c.Text, current, StringComparison.Ordinal));
        DbTextRepairCandidate? big5Candidate = candidates.FirstOrDefault(
            c => c.Source.IndexOf("big5-carrier-to-gbk", StringComparison.OrdinalIgnoreCase) >= 0
                 && !string.Equals(c.Text, current, StringComparison.Ordinal));

        if (big5Candidate == null)
        {
            Skip(result, context, "no-big5-candidate", currentCandidate, null);
            return;
        }

        result.Big5Candidates++;
        if (HasControlChars(current) || HasControlChars(big5Candidate.Text))
        {
            Skip(result, context, "control-character", currentCandidate, big5Candidate);
            return;
        }

        if (!IsBig5GbkRoundTrip(current, big5Candidate.Text))
        {
            Skip(result, context, "roundtrip-failed", currentCandidate, big5Candidate);
            return;
        }

        if (index.TryFindExact(
                context.DrawingSha256,
                context.Handle,
                current,
                big5Candidate.Text,
                out _,
                out bool hasConflict))
        {
            Skip(result, context, "existing-label", currentCandidate, big5Candidate);
            return;
        }

        if (hasConflict)
        {
            Skip(result, context, "conflict", currentCandidate, big5Candidate);
            return;
        }

        if (advisor.HasActiveNeuralRanker)
        {
            if (currentCandidate == null
                || !currentCandidate.HasNeuralScore
                || !big5Candidate.HasNeuralScore)
            {
                Skip(result, context, "missing-neural-score", currentCandidate, big5Candidate);
                return;
            }

            if (big5Candidate.NeuralScore <= currentCandidate.NeuralScore)
            {
                Skip(result, context, "neural-score-not-better", currentCandidate, big5Candidate);
                return;
            }
        }

        var record = new DbTextRepairModelRecord
        {
            RecordType = DbTextRepairModelConstants.RecordTypeLabel,
            SourceSetId = SourceSetId,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            DrawingPath = context.DrawingPath,
            DrawingFileName = context.DrawingFileName,
            DrawingLength = context.DrawingLength,
            DrawingLastWriteUtc = context.DrawingLastWriteUtc,
            DrawingSha256 = context.DrawingSha256,
            EntityType = context.EntityType,
            ObjectId = context.ObjectId,
            Handle = context.Handle,
            Layer = context.Layer,
            OwnerBlockName = context.OwnerBlockName,
            TextStyleName = context.TextStyleName,
            TextStyleFileName = context.TextStyleFileName,
            TextStyleBigFontFileName = context.TextStyleBigFontFileName,
            TextStyleTypeFace = context.TextStyleTypeFace,
            CurrentText = current,
            CandidateText = big5Candidate.Text,
            SelectedText = big5Candidate.Text,
            Action = DbTextRepairModelConstants.ActionRepair,
            Note = BatchNote
        };

        result.Labels.Add(record);
        result.Rows.Add(BatchAuditRow.FromContext("written", "accepted", context, currentCandidate, big5Candidate));
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

    private static bool IsAllowedProblemStyle(DbTextRepairModelRecord context)
    {
        string styleName = (context.TextStyleName ?? string.Empty).Trim();
        string fileName = NormalizeFontFileName(context.TextStyleFileName);
        string bigFontName = NormalizeFontFileName(context.TextStyleBigFontFileName);

        return string.Equals(styleName, "_HZTXT", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(fileName, "txt.shx", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(bigFontName, "tssdchn.shx", StringComparison.OrdinalIgnoreCase)
               || string.Equals(styleName, "HZTXT", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(fileName, "GBENOR", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(bigFontName, "tssdchn.shx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBig5GbkRoundTrip(string current, string candidate)
    {
        try
        {
            EnsureCodePages();
            Encoding big5 = Encoding.GetEncoding(
                950,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            Encoding gbk = Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);

            byte[] currentBytes = big5.GetBytes(current);
            byte[] candidateBytes = gbk.GetBytes(candidate);
            if (!currentBytes.SequenceEqual(candidateBytes))
                return false;

            string roundTrip = big5.GetString(candidateBytes);
            return string.Equals(roundTrip, current, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureCodePages()
    {
#if !NETFRAMEWORK
        if (Interlocked.Exchange(ref _providerRegistered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
    }

    private static bool HasControlChars(string text)
    {
        foreach (char ch in text)
        {
            if (char.IsControl(ch))
                return true;
        }

        return false;
    }

    private static void Skip(
        BatchScanResult result,
        DbTextRepairModelRecord context,
        string reason,
        DbTextRepairCandidate? currentCandidate = null,
        DbTextRepairCandidate? big5Candidate = null)
    {
        result.Skipped++;
        result.Rows.Add(BatchAuditRow.FromContext("skipped", reason, context, currentCandidate, big5Candidate));
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

    private static string NormalizeFontFileName(string value)
    {
        string text = (value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        try
        {
            string fileName = Path.GetFileName(text);
            return string.IsNullOrEmpty(fileName) ? text : fileName;
        }
        catch
        {
            return text;
        }
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

    private static string WriteAuditReport(BatchScanResult result, string trainingStatus)
    {
        string reportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CADFontAutoReplace",
            "Reports");
        Directory.CreateDirectory(reportDir);

        string path = Path.Combine(reportDir, $"DbTextBatchTrain_{DateTime.Now:yyyyMMdd_HHmmss}.tsv");
        var sb = new StringBuilder(result.Rows.Count * 256);
        sb.AppendLine("# AFR DBText batch train audit");
        sb.AppendLine("# Mode=batch-big5-conservative; no-drawing-write");
        sb.AppendLine("# DrawingPath=" + EscapeMeta(result.Drawing.Path));
        sb.AppendLine("# DrawingSha256=" + EscapeMeta(result.Drawing.Sha256));
        sb.AppendLine("# ModelPath=" + EscapeMeta(result.ModelPath));
        sb.AppendLine("# InitialModel=" + EscapeMeta(result.InitialModelSummary));
        sb.AppendLine("# NeuralRanker=" + EscapeMeta(result.NeuralRankerStatus));
        sb.AppendLine("# TrainingStatus=" + EscapeMeta(trainingStatus));
        sb.AppendLine($"# Scanned={result.Scanned}; Big5Candidates={result.Big5Candidates}; Written={result.Labels.Count}; Skipped={result.Skipped}; Errors={result.Errors}");
        AppendTsv(
            sb,
            "Status",
            "Reason",
            "Handle",
            "Layer",
            "Block",
            "TextStyle",
            "Font",
            "BigFont",
            "CurrentScore",
            "CandidateScore",
            "CurrentText",
            "CandidateText");

        foreach (BatchAuditRow row in result.Rows)
        {
            AppendTsv(
                sb,
                row.Status,
                row.Reason,
                row.Handle,
                row.Layer,
                row.OwnerBlockName,
                row.TextStyleName,
                row.TextStyleFileName,
                row.TextStyleBigFontFileName,
                FormatScore(row.CurrentScore),
                FormatScore(row.CandidateScore),
                row.CurrentText,
                row.CandidateText);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static void AppendTsv(StringBuilder sb, params string[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
                sb.Append('\t');
            sb.Append(EscapeTsv(values[i]));
        }

        sb.AppendLine();
    }

    private static string EscapeMeta(string value)
    {
        return (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string EscapeTsv(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\t", "\\t")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static string FormatScore(float? value)
    {
        return value.HasValue ? value.Value.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty;
    }

    private readonly record struct TextStyleIdentity(
        string Name,
        string FileName,
        string BigFontFileName,
        string TypeFace);

    private sealed class BatchScanResult
    {
        public BatchScanResult(
            DbTextDrawingIdentity drawing,
            string neuralRankerStatus,
            string modelPath,
            string initialModelSummary)
        {
            Drawing = drawing;
            NeuralRankerStatus = neuralRankerStatus;
            ModelPath = modelPath;
            InitialModelSummary = initialModelSummary;
        }

        public DbTextDrawingIdentity Drawing { get; }
        public string NeuralRankerStatus { get; }
        public string ModelPath { get; }
        public string InitialModelSummary { get; }
        public int Scanned { get; set; }
        public int Big5Candidates { get; set; }
        public int Skipped { get; set; }
        public int Errors { get; set; }
        public List<DbTextRepairModelRecord> Labels { get; } = new();
        public List<BatchAuditRow> Rows { get; } = new();
    }

    private sealed class BatchAuditRow
    {
        public string Status { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public string Handle { get; init; } = string.Empty;
        public string Layer { get; init; } = string.Empty;
        public string OwnerBlockName { get; init; } = string.Empty;
        public string TextStyleName { get; init; } = string.Empty;
        public string TextStyleFileName { get; init; } = string.Empty;
        public string TextStyleBigFontFileName { get; init; } = string.Empty;
        public string CurrentText { get; init; } = string.Empty;
        public string CandidateText { get; init; } = string.Empty;
        public float? CurrentScore { get; init; }
        public float? CandidateScore { get; init; }

        public static BatchAuditRow FromContext(
            string status,
            string reason,
            DbTextRepairModelRecord context,
            DbTextRepairCandidate? currentCandidate,
            DbTextRepairCandidate? big5Candidate)
        {
            return new BatchAuditRow
            {
                Status = status,
                Reason = reason,
                Handle = context.Handle,
                Layer = context.Layer,
                OwnerBlockName = context.OwnerBlockName,
                TextStyleName = context.TextStyleName,
                TextStyleFileName = context.TextStyleFileName,
                TextStyleBigFontFileName = context.TextStyleBigFontFileName,
                CurrentText = context.CurrentText,
                CandidateText = big5Candidate?.Text ?? string.Empty,
                CurrentScore = currentCandidate?.HasNeuralScore == true ? currentCandidate.NeuralScore : null,
                CandidateScore = big5Candidate?.HasNeuralScore == true ? big5Candidate.NeuralScore : null
            };
        }

        public static BatchAuditRow Error(string reason, string message)
        {
            return new BatchAuditRow
            {
                Status = "error",
                Reason = reason,
                CurrentText = message
            };
        }
    }
}
#endif
