#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AFR.GlyphCore.TextRepair;
using AFR.Services.GlyphCore.TextRepair;
using Newtonsoft.Json;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.DebugCommands.GlyphCoreExportCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// DEBUG only: export a read-only GlyphCore DBText dataset package.
/// </summary>
internal sealed class GlyphCoreExportCommand
{
    [CommandMethod(AFR.Constants.CommandNames.ExportGlyphCoreDataset)]
    public void Export()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        Editor? editor = doc?.Editor;
        if (doc == null || editor == null)
            return;

        try
        {
            using (doc.LockDocument())
            {
                GlyphCoreDatasetExporter exporter = new();
                GlyphCoreExportResult result = exporter.Export(doc.Database);
                WriteExportResult(editor, result);
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\n[AFR 文枢] 文枢 DBText 数据包导出失败：{0}: {1}\n", ex.GetType().Name, ex.Message);
        }
    }

    [CommandMethod(AFR.Constants.CommandNames.ExportGlyphCoreSelectedDataset)]
    public void ExportSelected()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        Editor? editor = doc?.Editor;
        if (doc == null || editor == null)
            return;

        try
        {
            ObjectId[] selectedIds = PromptDbTextSelection(editor);
            if (selectedIds.Length == 0)
            {
                editor.WriteMessage("\n[AFR 文枢] 未选择 DBText，未导出数据包。\n");
                return;
            }

            if (!ConfirmExport(editor, selectedIds.Length))
            {
                editor.WriteMessage("\n[AFR 文枢] 已取消导出，未生成数据包。\n");
                return;
            }

            using (doc.LockDocument())
            {
                GlyphCoreDatasetExporter exporter = new();
                GlyphCoreExportResult result = exporter.ExportSelected(doc.Database, selectedIds);
                WriteExportResult(editor, result);
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\n[AFR 文枢] 文枢 DBText 数据包导出失败：{0}: {1}\n", ex.GetType().Name, ex.Message);
        }
    }

    private static ObjectId[] PromptDbTextSelection(Editor editor)
    {
        PromptSelectionOptions options = new()
        {
            AllowDuplicates = false,
            MessageForAdding = "\n选择需要导出的 DBText，按 Enter 确认: "
        };
        SelectionFilter filter = new(new[]
        {
            new TypedValue((int)DxfCode.Start, "TEXT")
        });

        PromptSelectionResult result = editor.GetSelection(options, filter);
        if (result.Status != PromptStatus.OK || result.Value == null)
            return Array.Empty<ObjectId>();

        return result.Value.GetObjectIds()
            .Where(id => !id.IsNull && id.IsValid)
            .Distinct()
            .ToArray();
    }

    private static bool ConfirmExport(Editor editor, int count)
    {
        PromptKeywordOptions options = new($"\n已选择 {count} 个 DBText。确认生成 AI 分析数据包? [Yes/No] <Yes>: ")
        {
            AllowNone = true
        };
        options.Keywords.Add("Yes");
        options.Keywords.Add("No");
        options.Keywords.Default = "Yes";

        PromptResult result = editor.GetKeywords(options);
        if (result.Status == PromptStatus.None)
            return true;
        if (result.Status != PromptStatus.OK)
            return false;

        return string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteExportResult(Editor editor, GlyphCoreExportResult result)
    {
        editor.WriteMessage(
            "\n[AFR 文枢] 已导出文枢 DBText 数据包：{0}\n扫描={1}, 导出={2}, Hook强信号={3}, 空文本跳过={4}, 错误={5}\n下一步：运行 AFR.GlyphCore\\tools\\Start-GlyphCoreWorkbench.cmd -Package \"{0}\"\n",
            result.PackageDirectory,
            result.Scanned,
            result.Exported,
            result.ProblemCount,
            result.EmptySkipped,
            result.Errors);
    }
}

internal sealed class GlyphCoreDatasetExporter
{
    private const string PackageSchema = "dbtext-ai-export-package-v1";
    private const string CandidateGroupSchema = "dbtext-ai-candidate-group-v1";
    private const string ReviewedLabelSchema = "dbtext-ai-reviewed-label-v1";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding Utf8Bom = new(true);

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore
    };

    private static readonly JsonSerializerSettings PrettyJsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore
    };

    public GlyphCoreExportResult Export(Database db)
    {
        return ExportCore(db, selectedIds: null, AFR.Constants.CommandNames.ExportGlyphCoreDataset);
    }

    public GlyphCoreExportResult ExportSelected(Database db, IEnumerable<ObjectId> selectedIds)
    {
        if (selectedIds == null)
            throw new ArgumentNullException(nameof(selectedIds));

        ObjectId[] ids = selectedIds
            .Where(id => !id.IsNull && id.IsValid)
            .Distinct()
            .ToArray();

        return ExportCore(db, ids, AFR.Constants.CommandNames.ExportGlyphCoreSelectedDataset);
    }

    private GlyphCoreExportResult ExportCore(Database db, IReadOnlyCollection<ObjectId>? selectedIds, string commandName)
    {
        if (db == null)
            throw new ArgumentNullException(nameof(db));

        GlyphCoreDrawingIdentity drawing = GlyphCoreDrawingIdentity.FromDatabase(db);
        string datasetRoot = ResolveDatasetRoot(db);
        string exportRoot = Path.Combine(datasetRoot, "ExtractedCandidates");
        Directory.CreateDirectory(exportRoot);

        string exportId = BuildExportId(drawing);
        string packageDirectory = Path.Combine(exportRoot, exportId);
        Directory.CreateDirectory(packageDirectory);

        string candidatePath = Path.Combine(packageDirectory, "candidate_groups.jsonl");
        string auditPath = Path.Combine(packageDirectory, "audit.tsv");
        string previewPath = Path.Combine(packageDirectory, "preview.json");
        string manifestPath = Path.Combine(packageDirectory, "manifest.json");

        int scanned = 0;
        int exported = 0;
        int problems = 0;
        int emptySkipped = 0;
        int errors = 0;
        int xrefCount = 0;
        int unsafeCount = 0;
        int nonRoundTripCount = 0;

        List<object> previewItems = new();
        GlyphCoreTextRepairAdvisor? advisor = null;
        string aiStatus = "not-invoked";

        using var candidateWriter = new StreamWriter(candidatePath, append: false, Utf8NoBom);
        using var auditWriter = new StreamWriter(auditPath, append: false, Utf8Bom);
        WriteAuditHeader(auditWriter);

        using var tr = db.TransactionManager.StartTransaction();
        void ExportEntity(ObjectId entityId)
        {
            try
            {
                if (tr.GetObject(entityId, OpenMode.ForRead, false, true) is not DBText dbText)
                    return;

                scanned++;
                string current = dbText.TextString ?? string.Empty;
                if (string.IsNullOrWhiteSpace(current))
                {
                    emptySkipped++;
                    return;
                }

                GlyphCoreTextRepairContext context = GlyphCoreTextRepairEntitySnapshotBuilder.BuildContext(db, tr, dbText, drawing);
                GlyphCoreTextRepairProblemDetection detection = GlyphCoreTextRepairProblemDetector.Detect(context);
                if (detection.HasProblem)
                    problems++;

                IReadOnlyList<GlyphCoreTextRepairCandidate> candidates = detection.Candidates;
                if (candidates.Count == 0)
                {
                    emptySkipped++;
                    return;
                }

                advisor ??= new GlyphCoreTextRepairAdvisor();
                aiStatus = advisor.AiStatus;
                if (advisor.IsAiAvailable)
                    advisor.ScoreCandidates(context, candidates);

                bool currentUnsafe = GlyphCoreTextRepairFeatureExtractor.HasUnsafeText(context.CurrentText);
                bool candidateUnsafe = candidates.Any(c => GlyphCoreTextRepairFeatureExtractor.HasUnsafeText(c.Text));
                bool hasNonRoundTrip = candidates.Any(c => !c.IsNoOp && !c.IsRoundTrip);
                bool candidateConflict = candidates.Where(c => !c.IsNoOp).Select(c => c.Text).Distinct(StringComparer.Ordinal).Count() > 1;

                if (context.IsFromExternalReference)
                    xrefCount++;
                if (currentUnsafe || candidateUnsafe)
                    unsafeCount++;
                if (hasNonRoundTrip)
                    nonRoundTripCount++;

                string groupId = BuildGroupId(drawing, context);
                GlyphCoreTextRepairGeometrySnapshot geometry = BuildGeometry(dbText);
                object row = BuildCandidateGroupRow(
                    exportId,
                    groupId,
                    drawing,
                    context,
                    detection,
                    candidates,
                    geometry,
                    currentUnsafe,
                    candidateUnsafe,
                    hasNonRoundTrip,
                    candidateConflict);

                candidateWriter.WriteLine(JsonConvert.SerializeObject(row, JsonSettings));
                WriteAuditRow(auditWriter, groupId, context, detection, candidates, geometry);
                previewItems.Add(BuildPreviewItem(groupId, context, detection, geometry));
                exported++;
            }
            catch
            {
                errors++;
            }
        }

        if (selectedIds == null)
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            foreach (ObjectId blockId in blockTable)
            {
                BlockTableRecord block;
                try
                {
                    block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                }
                catch
                {
                    errors++;
                    continue;
                }

                foreach (ObjectId entityId in block)
                    ExportEntity(entityId);
            }
        }
        else
        {
            foreach (ObjectId entityId in selectedIds)
                ExportEntity(entityId);
        }

        tr.Commit();

        WriteJson(previewPath, new
        {
            schema = "dbtext-ai-preview-v1",
            exportId,
            drawing = BuildDrawingObject(drawing),
            items = previewItems
        });

        WriteJson(manifestPath, new
        {
            schema = PackageSchema,
            candidateGroupSchema = CandidateGroupSchema,
            reviewedLabelSchema = ReviewedLabelSchema,
            featureSchema = GlyphCoreTextRepairConstants.FeatureSchemaVersion,
            aiDisplayName = "文枢",
            aiInternalName = "GlyphCore",
            exportId,
            createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            afrVersion = Safe(() => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty, string.Empty),
            commandName,
            packageDirectory,
            files = new
            {
                candidateGroups = "candidate_groups.jsonl",
                audit = "audit.tsv",
                preview = "preview.json"
            },
            drawing = BuildDrawingObject(drawing),
            ai = new
            {
                status = aiStatus,
                modelScoringAttempted = !string.Equals(aiStatus, "not-invoked", StringComparison.Ordinal)
            },
            counts = new
            {
                scanned,
                exported,
                suspectedProblems = problems,
                emptySkipped,
                xref = xrefCount,
                unsafeText = unsafeCount,
                nonRoundTrip = nonRoundTripCount,
                errors
            },
            safety = new
            {
                readOnlyExport = true,
                dwgWriteBack = false,
                localOnly = true,
                requiresHumanReviewBeforeTraining = true
            }
        });

        return new GlyphCoreExportResult(packageDirectory, scanned, exported, problems, emptySkipped, errors);
    }

    private static object BuildCandidateGroupRow(
        string exportId,
        string groupId,
        GlyphCoreDrawingIdentity drawing,
        GlyphCoreTextRepairContext context,
        GlyphCoreTextRepairProblemDetection detection,
        IReadOnlyList<GlyphCoreTextRepairCandidate> candidates,
        GlyphCoreTextRepairGeometrySnapshot geometry,
        bool currentUnsafe,
        bool candidateUnsafe,
        bool hasNonRoundTrip,
        bool candidateConflict)
    {
        string displayText = BuildDisplayText(context.CurrentText);
        bool hasDisplayAlias = !string.Equals(displayText, context.CurrentText, StringComparison.Ordinal);
        return new
        {
            schema = CandidateGroupSchema,
            featureSchema = GlyphCoreTextRepairConstants.FeatureSchemaVersion,
            exportId,
            groupId,
            drawing = BuildDrawingObject(drawing),
            context = BuildContextObject(context),
            geometry,
            currentText = context.CurrentText,
            rawCurrentText = context.CurrentText,
            displayText,
            displayAlias = new
            {
                normalized = hasDisplayAlias,
                kind = hasDisplayAlias ? "shx-number-sign-token" : string.Empty
            },
            problemGate = new
            {
                hasProblem = detection.HasProblem,
                reason = detection.Reason,
                requiresNativeDecodeEvidence = true
            },
            risk = new
            {
                isFromXref = context.IsFromExternalReference,
                currentUnsafe,
                candidateUnsafe,
                hasNonRoundTrip,
                candidateConflict,
                highRisk = context.IsFromExternalReference || currentUnsafe || candidateUnsafe || candidateConflict
            },
            candidates = candidates.Select((candidate, index) => new
            {
                index,
                text = candidate.Text,
                source = candidate.Source,
                reason = candidate.Reason,
                isRoundTrip = candidate.IsRoundTrip,
                isNoOp = candidate.IsNoOp,
                hasAiScore = candidate.HasAiScore,
                aiScore = candidate.HasAiScore ? Math.Round(candidate.AiScore, 6) : (double?)null,
                unsafeText = GlyphCoreTextRepairFeatureExtractor.HasUnsafeText(candidate.Text),
                features = GlyphCoreTextRepairFeatureExtractor.Extract(context, candidate).Select(v => Math.Round(v, 6)).ToArray()
            }).ToArray()
        };
    }

    private static object BuildDrawingObject(GlyphCoreDrawingIdentity drawing)
    {
        return new
        {
            path = drawing.Path,
            fileName = drawing.FileName,
            length = drawing.Length,
            lastWriteUtc = drawing.LastWriteUtc,
            sha256 = drawing.Sha256
        };
    }

    private static object BuildContextObject(GlyphCoreTextRepairContext context)
    {
        string displayText = BuildDisplayText(context.CurrentText);
        bool hasDisplayAlias = !string.Equals(displayText, context.CurrentText, StringComparison.Ordinal);
        return new
        {
            drawingPath = context.DrawingPath,
            drawingFileName = context.DrawingFileName,
            drawingLength = context.DrawingLength,
            drawingLastWriteUtc = context.DrawingLastWriteUtc,
            drawingSha256 = context.DrawingSha256,
            entityType = context.EntityType,
            objectId = context.ObjectId,
            handle = context.Handle,
            layer = context.Layer,
            ownerBlockName = context.OwnerBlockName,
            textStyleName = context.TextStyleName,
            textStyleFileName = context.TextStyleFileName,
            textStyleBigFontFileName = context.TextStyleBigFontFileName,
            textStyleTypeFace = context.TextStyleTypeFace,
            currentText = context.CurrentText,
            rawCurrentText = context.CurrentText,
            displayText,
            displayAlias = new
            {
                normalized = hasDisplayAlias,
                kind = hasDisplayAlias ? "shx-number-sign-token" : string.Empty
            },
            isFromExternalReference = context.IsFromExternalReference,
            nativeDecodeEvidence = new
            {
                hasEvidence = context.HasNativeDecodeEvidence,
                familyMismatch = context.NativeDecodeFamilyMismatch,
                scope = context.NativeDecodeEvidenceScope,
                clusterKey = context.NativeDecodeEvidenceClusterKey,
                sourceCodePageFamily = context.NativeDecodeSourceCodePageFamily,
                appliedCodePageFamily = context.NativeDecodeAppliedCodePageFamily,
                hookHitType = context.NativeDecodeHookHitType,
                objectCorrelation = Math.Round(context.NativeDecodeObjectCorrelation, 6),
                clusterCorrelation = Math.Round(context.NativeDecodeClusterCorrelation, 6),
                hasLdFileFontEvidence = context.HasLdFileFontEvidence,
                hasHookRawDecodeEvidence = context.HasHookRawDecodeEvidence,
                hookRawPayloadSha256 = context.HookRawPayloadSha256,
                hookRawPayloadLength = context.HookRawPayloadLength,
                hookPreferredDecodedText = context.HookPreferredDecodedText,
                hookRawCandidateSource = context.HookRawCandidateSource,
                hookRawRoundTrip = context.HookRawRoundTrip,
                hookRawConfidence = Math.Round(context.HookRawConfidence, 6),
                rippleSeedCount = context.RippleSeedCount,
                rippleContextText = context.RippleContextText,
                rippleSeedQuality = Math.Round(context.RippleSeedQuality, 6),
                rippleDistanceRatio = Math.Round(context.RippleDistanceRatio, 6)
            }
        };
    }

    private static GlyphCoreTextRepairGeometrySnapshot BuildGeometry(DBText dbText)
    {
        GlyphCoreTextRepairExtentsSnapshot? extents = null;
        try
        {
            Extents3d geo = dbText.GeometricExtents;
            extents = new GlyphCoreTextRepairExtentsSnapshot(
                Point(geo.MinPoint.X, geo.MinPoint.Y, geo.MinPoint.Z),
                Point(geo.MaxPoint.X, geo.MaxPoint.Y, geo.MaxPoint.Z));
        }
        catch
        {
            // Some DBText instances cannot report extents until regenerated.
        }

        return new GlyphCoreTextRepairGeometrySnapshot(
            Point(
                Safe(() => dbText.Position.X, 0.0),
                Safe(() => dbText.Position.Y, 0.0),
                Safe(() => dbText.Position.Z, 0.0)),
            Point(
                Safe(() => dbText.AlignmentPoint.X, 0.0),
                Safe(() => dbText.AlignmentPoint.Y, 0.0),
                Safe(() => dbText.AlignmentPoint.Z, 0.0)),
            Point(
                Safe(() => dbText.Normal.X, 0.0),
                Safe(() => dbText.Normal.Y, 0.0),
                Safe(() => dbText.Normal.Z, 1.0)),
            Safe(() => dbText.Height, 0.0),
            Safe(() => dbText.Rotation, 0.0),
            Safe(() => dbText.WidthFactor, 0.0),
            Safe(() => dbText.Oblique, 0.0),
            Safe(() => dbText.HorizontalMode.ToString(), string.Empty),
            Safe(() => dbText.VerticalMode.ToString(), string.Empty),
            Safe(() => dbText.IsMirroredInX, false),
            Safe(() => dbText.IsMirroredInY, false),
            extents);
    }

    private static object BuildPreviewItem(
        string groupId,
        GlyphCoreTextRepairContext context,
        GlyphCoreTextRepairProblemDetection detection,
        GlyphCoreTextRepairGeometrySnapshot geometry)
    {
        string displayText = BuildDisplayText(context.CurrentText);
        return new
        {
            groupId,
            handle = context.Handle,
            text = Trim(displayText, 80),
            rawText = context.CurrentText,
            layer = context.Layer,
            ownerBlockName = context.OwnerBlockName,
            textStyleName = context.TextStyleName,
            isFromExternalReference = context.IsFromExternalReference,
            hasProblem = detection.HasProblem,
            problemReason = detection.Reason,
            nativeDecodeEvidence = new
            {
                hasEvidence = context.HasNativeDecodeEvidence,
                scope = context.NativeDecodeEvidenceScope,
                sourceCodePageFamily = context.NativeDecodeSourceCodePageFamily,
                appliedCodePageFamily = context.NativeDecodeAppliedCodePageFamily
            },
            geometry = new
            {
                position = geometry.Position,
                extents = geometry.Extents
            }
        };
    }

    private static void WriteAuditHeader(TextWriter writer)
    {
        writer.WriteLine(string.Join("\t", new[]
        {
            "group_id",
            "handle",
            "layer",
            "owner_block",
            "text_style",
            "font",
            "bigfont",
            "is_xref",
            "has_problem",
            "problem_reason",
            "native_decode_evidence",
            "native_decode_scope",
            "native_decode_source_family",
            "native_decode_applied_family",
            "native_decode_hook_hit",
            "hook_raw_evidence",
            "hook_raw_payload_length",
            "hook_raw_confidence",
            "candidate_count",
            "current_text",
            "display_text",
            "display_alias",
            "position_x",
            "position_y",
            "position_z"
        }));
    }

    private static void WriteAuditRow(
        TextWriter writer,
        string groupId,
        GlyphCoreTextRepairContext context,
        GlyphCoreTextRepairProblemDetection detection,
        IReadOnlyList<GlyphCoreTextRepairCandidate> candidates,
        GlyphCoreTextRepairGeometrySnapshot geometry)
    {
        string displayText = BuildDisplayText(context.CurrentText);
        bool hasDisplayAlias = !string.Equals(displayText, context.CurrentText, StringComparison.Ordinal);
        writer.WriteLine(string.Join("\t", new[]
        {
            EscapeTsv(groupId),
            EscapeTsv(context.Handle),
            EscapeTsv(context.Layer),
            EscapeTsv(context.OwnerBlockName),
            EscapeTsv(context.TextStyleName),
            EscapeTsv(context.TextStyleFileName),
            EscapeTsv(context.TextStyleBigFontFileName),
            context.IsFromExternalReference ? "1" : "0",
            detection.HasProblem ? "1" : "0",
            EscapeTsv(detection.Reason),
            context.HasNativeDecodeEvidence ? "1" : "0",
            EscapeTsv(context.NativeDecodeEvidenceScope),
            EscapeTsv(context.NativeDecodeSourceCodePageFamily),
            EscapeTsv(context.NativeDecodeAppliedCodePageFamily),
            EscapeTsv(context.NativeDecodeHookHitType),
            context.HasHookRawDecodeEvidence ? "1" : "0",
            context.HookRawPayloadLength.ToString(CultureInfo.InvariantCulture),
            Number(context.HookRawConfidence),
            candidates.Count.ToString(CultureInfo.InvariantCulture),
            EscapeTsv(context.CurrentText),
            EscapeTsv(displayText),
            hasDisplayAlias ? "shx-number-sign-token" : string.Empty,
            Number(geometry.Position.X),
            Number(geometry.Position.Y),
            Number(geometry.Position.Z)
        }));
    }

    private static string ResolveDatasetRoot(Database db)
    {
        string? explicitRoot = Environment.GetEnvironmentVariable("AFR_GLYPHCORE_DATASET_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return explicitRoot;

        IEnumerable<string?> starts = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            Environment.CurrentDirectory,
            Path.GetDirectoryName(db.Filename ?? string.Empty)
        };

        foreach (string? start in starts)
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            DirectoryInfo? dir = new(start);
            while (dir != null)
            {
                string repoDatasetRoot = Path.Combine(dir.FullName, "AFR.GlyphCore", "datasets");
                if (Directory.Exists(repoDatasetRoot) || File.Exists(Path.Combine(dir.FullName, "CADFontAutoReplace.slnx")))
                    return repoDatasetRoot;

                dir = dir.Parent;
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CADFontAutoReplace",
            "GlyphCore",
            "Datasets");
    }

    private static string BuildExportId(GlyphCoreDrawingIdentity drawing)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string name = string.IsNullOrWhiteSpace(drawing.FileName) ? "unsaved-drawing" : Path.GetFileNameWithoutExtension(drawing.FileName);
        string hash = string.IsNullOrWhiteSpace(drawing.Sha256) ? "nohash" : drawing.Sha256[..Math.Min(8, drawing.Sha256.Length)].ToLowerInvariant();
        return $"{stamp}_{SanitizeFileName(name)}_{hash}";
    }

    private static string BuildGroupId(GlyphCoreDrawingIdentity drawing, GlyphCoreTextRepairContext context)
    {
        string identity = string.Join("|", new[]
        {
            drawing.Sha256,
            drawing.FileName,
            context.Handle,
            context.OwnerBlockName,
            context.CurrentText
        });
        return Sha256Hex(identity)[..32].ToLowerInvariant();
    }

    private static string Sha256Hex(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        return builder.ToString();
    }

    private static void WriteJson(string path, object value)
    {
        File.WriteAllText(path, JsonConvert.SerializeObject(value, PrettyJsonSettings), Utf8NoBom);
    }

    private static GlyphCoreTextRepairPointSnapshot Point(double x, double y, double z)
    {
        return new GlyphCoreTextRepairPointSnapshot(
            Math.Round(x, 6),
            Math.Round(y, 6),
            Math.Round(z, 6));
    }

    private static string EscapeTsv(string text)
    {
        return (text ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Trim(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        return text[..maxLength] + "...";
    }

    private static string BuildDisplayText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\u4E95') < 0)
            return text ?? string.Empty;

        StringBuilder? builder = null;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '\u4E95' || !ShouldRenderNumberSignAlias(text, index))
                continue;

            builder ??= new StringBuilder(text);
            builder[index] = '#';
        }

        return builder?.ToString() ?? text;
    }

    private static bool ShouldRenderNumberSignAlias(string text, int index)
    {
        if (index < 2 || index + 1 >= text.Length)
            return false;

        char previous = text[index - 1];
        if (previous != '-' && previous != '\uFF0D')
            return false;

        if (!IsAsciiLetterOrDigit(text[index + 1]))
            return false;

        int start = index - 2;
        while (start >= 0 && IsAsciiLetterOrDigit(text[start]))
            start--;

        int length = index - start - 2;
        if (length < 1 || length > 8)
            return false;

        bool hasAsciiLetter = false;
        for (int scan = start + 1; scan <= index - 2; scan++)
        {
            if (IsAsciiLetter(text[scan]))
            {
                hasAsciiLetter = true;
                break;
            }
        }

        return hasAsciiLetter;
    }

    private static bool IsAsciiLetterOrDigit(char value)
        => IsAsciiLetter(value) || (value >= '0' && value <= '9');

    private static bool IsAsciiLetter(char value)
        => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}

internal readonly record struct GlyphCoreExportResult(
    string PackageDirectory,
    int Scanned,
    int Exported,
    int ProblemCount,
    int EmptySkipped,
    int Errors);

internal readonly record struct GlyphCoreTextRepairPointSnapshot(
    [property: JsonProperty("x")] double X,
    [property: JsonProperty("y")] double Y,
    [property: JsonProperty("z")] double Z);

internal readonly record struct GlyphCoreTextRepairExtentsSnapshot(
    [property: JsonProperty("min")] GlyphCoreTextRepairPointSnapshot Min,
    [property: JsonProperty("max")] GlyphCoreTextRepairPointSnapshot Max);

internal readonly record struct GlyphCoreTextRepairGeometrySnapshot(
    [property: JsonProperty("position")] GlyphCoreTextRepairPointSnapshot Position,
    [property: JsonProperty("alignmentPoint")] GlyphCoreTextRepairPointSnapshot AlignmentPoint,
    [property: JsonProperty("normal")] GlyphCoreTextRepairPointSnapshot Normal,
    [property: JsonProperty("height")] double Height,
    [property: JsonProperty("rotation")] double Rotation,
    [property: JsonProperty("widthFactor")] double WidthFactor,
    [property: JsonProperty("oblique")] double Oblique,
    [property: JsonProperty("horizontalMode")] string HorizontalMode,
    [property: JsonProperty("verticalMode")] string VerticalMode,
    [property: JsonProperty("isMirroredInX")] bool IsMirroredInX,
    [property: JsonProperty("isMirroredInY")] bool IsMirroredInY,
    [property: JsonProperty("extents")] GlyphCoreTextRepairExtentsSnapshot? Extents);
#endif

