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
using AFR.WenShu.DbText;
using AFR.Services.WenShu.DbText;
using Newtonsoft.Json;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.DebugCommands.WenShuDbTextExportCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// DEBUG only: export a read-only WenShu DBText dataset package.
/// </summary>
internal sealed class WenShuDbTextExportCommand
{
    [CommandMethod(AFR.Constants.CommandNames.ExportWenShuDbTextDataset)]
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
                WenShuDbTextDatasetExporter exporter = new();
                WenShuDbTextExportResult result = exporter.Export(doc.Database);
                editor.WriteMessage(
                    "\n[AFR 文枢] 已导出文枢 DBText 数据包：{0}\n扫描={1}, 导出={2}, 疑似异常={3}, 空文本跳过={4}, 错误={5}\n下一步：运行 tools\\WenShu\\DbText\\Start-WenShuWorkbench.ps1 -Package \"{0}\"\n",
                    result.PackageDirectory,
                    result.Scanned,
                    result.Exported,
                    result.ProblemCount,
                    result.EmptySkipped,
                    result.Errors);
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\n[AFR 文枢] 文枢 DBText 数据包导出失败：{0}: {1}\n", ex.GetType().Name, ex.Message);
        }
    }
}

internal sealed class WenShuDbTextDatasetExporter
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

    public WenShuDbTextExportResult Export(Database db)
    {
        if (db == null)
            throw new ArgumentNullException(nameof(db));

        WenShuDrawingIdentity drawing = WenShuDrawingIdentity.FromDatabase(db);
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
        WenShuDbTextAdvisor? advisor = null;
        string aiStatus = "not-invoked";

        using var candidateWriter = new StreamWriter(candidatePath, append: false, Utf8NoBom);
        using var auditWriter = new StreamWriter(auditPath, append: false, Utf8Bom);
        WriteAuditHeader(auditWriter);

        using var tr = db.TransactionManager.StartTransaction();
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
            {
                try
                {
                    if (tr.GetObject(entityId, OpenMode.ForRead, false, true) is not DBText dbText)
                        continue;

                    scanned++;
                    string current = dbText.TextString ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(current))
                    {
                        emptySkipped++;
                        continue;
                    }

                    WenShuDbTextContext context = WenShuDbTextEntitySnapshotBuilder.BuildContext(db, tr, dbText, drawing);
                    WenShuDbTextProblemDetection detection = WenShuDbTextProblemDetector.Detect(context);
                    if (detection.HasProblem)
                        problems++;

                    IReadOnlyList<WenShuDbTextCandidate> candidates = detection.Candidates;
                    if (candidates.Count == 0)
                    {
                        emptySkipped++;
                        continue;
                    }

                    advisor ??= new WenShuDbTextAdvisor();
                    aiStatus = advisor.AiStatus;
                    if (advisor.IsAiAvailable)
                        advisor.ScoreCandidates(context, candidates);

                    bool currentUnsafe = WenShuDbTextFeatureExtractor.HasUnsafeText(context.CurrentText);
                    bool candidateUnsafe = candidates.Any(c => WenShuDbTextFeatureExtractor.HasUnsafeText(c.Text));
                    bool hasNonRoundTrip = candidates.Any(c => !c.IsNoOp && !c.IsRoundTrip);
                    bool candidateConflict = candidates.Where(c => !c.IsNoOp).Select(c => c.Text).Distinct(StringComparer.Ordinal).Count() > 1;

                    if (context.IsFromExternalReference)
                        xrefCount++;
                    if (currentUnsafe || candidateUnsafe)
                        unsafeCount++;
                    if (hasNonRoundTrip)
                        nonRoundTripCount++;

                    string groupId = BuildGroupId(drawing, context);
                    WenShuDbTextGeometrySnapshot geometry = BuildGeometry(dbText);
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
            featureSchema = WenShuDbTextConstants.FeatureSchemaVersion,
            aiDisplayName = "文枢",
            aiInternalName = "AFR DBText WenShu",
            exportId,
            createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            afrVersion = Safe(() => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty, string.Empty),
            commandName = AFR.Constants.CommandNames.ExportWenShuDbTextDataset,
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

        return new WenShuDbTextExportResult(packageDirectory, scanned, exported, problems, emptySkipped, errors);
    }

    private static object BuildCandidateGroupRow(
        string exportId,
        string groupId,
        WenShuDrawingIdentity drawing,
        WenShuDbTextContext context,
        WenShuDbTextProblemDetection detection,
        IReadOnlyList<WenShuDbTextCandidate> candidates,
        WenShuDbTextGeometrySnapshot geometry,
        bool currentUnsafe,
        bool candidateUnsafe,
        bool hasNonRoundTrip,
        bool candidateConflict)
    {
        return new
        {
            schema = CandidateGroupSchema,
            featureSchema = WenShuDbTextConstants.FeatureSchemaVersion,
            exportId,
            groupId,
            drawing = BuildDrawingObject(drawing),
            context = BuildContextObject(context),
            geometry,
            currentText = context.CurrentText,
            problemGate = new
            {
                hasProblem = detection.HasProblem,
                reason = detection.Reason
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
                unsafeText = WenShuDbTextFeatureExtractor.HasUnsafeText(candidate.Text),
                features = WenShuDbTextFeatureExtractor.Extract(context, candidate).Select(v => Math.Round(v, 6)).ToArray()
            }).ToArray()
        };
    }

    private static object BuildDrawingObject(WenShuDrawingIdentity drawing)
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

    private static object BuildContextObject(WenShuDbTextContext context)
    {
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
            isFromExternalReference = context.IsFromExternalReference
        };
    }

    private static WenShuDbTextGeometrySnapshot BuildGeometry(DBText dbText)
    {
        WenShuDbTextExtentsSnapshot? extents = null;
        try
        {
            Extents3d geo = dbText.GeometricExtents;
            extents = new WenShuDbTextExtentsSnapshot(
                Point(geo.MinPoint.X, geo.MinPoint.Y, geo.MinPoint.Z),
                Point(geo.MaxPoint.X, geo.MaxPoint.Y, geo.MaxPoint.Z));
        }
        catch
        {
            // Some DBText instances cannot report extents until regenerated.
        }

        return new WenShuDbTextGeometrySnapshot(
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
        WenShuDbTextContext context,
        WenShuDbTextProblemDetection detection,
        WenShuDbTextGeometrySnapshot geometry)
    {
        return new
        {
            groupId,
            handle = context.Handle,
            text = Trim(context.CurrentText, 80),
            layer = context.Layer,
            ownerBlockName = context.OwnerBlockName,
            textStyleName = context.TextStyleName,
            isFromExternalReference = context.IsFromExternalReference,
            hasProblem = detection.HasProblem,
            problemReason = detection.Reason,
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
            "candidate_count",
            "current_text",
            "position_x",
            "position_y",
            "position_z"
        }));
    }

    private static void WriteAuditRow(
        TextWriter writer,
        string groupId,
        WenShuDbTextContext context,
        WenShuDbTextProblemDetection detection,
        IReadOnlyList<WenShuDbTextCandidate> candidates,
        WenShuDbTextGeometrySnapshot geometry)
    {
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
            candidates.Count.ToString(CultureInfo.InvariantCulture),
            EscapeTsv(context.CurrentText),
            Number(geometry.Position.X),
            Number(geometry.Position.Y),
            Number(geometry.Position.Z)
        }));
    }

    private static string ResolveDatasetRoot(Database db)
    {
        string? explicitRoot = Environment.GetEnvironmentVariable("AFR_WENSHU_DBTEXT_DATASET_ROOT");
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
                string repoDatasetRoot = Path.Combine(dir.FullName, "datasets", "WenShu", "DbText");
                if (Directory.Exists(repoDatasetRoot) || File.Exists(Path.Combine(dir.FullName, "CADFontAutoReplace.slnx")))
                    return repoDatasetRoot;

                dir = dir.Parent;
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CADFontAutoReplace",
            "WenShu",
            "DbText",
            "Datasets");
    }

    private static string BuildExportId(WenShuDrawingIdentity drawing)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string name = string.IsNullOrWhiteSpace(drawing.FileName) ? "unsaved-drawing" : Path.GetFileNameWithoutExtension(drawing.FileName);
        string hash = string.IsNullOrWhiteSpace(drawing.Sha256) ? "nohash" : drawing.Sha256[..Math.Min(8, drawing.Sha256.Length)].ToLowerInvariant();
        return $"{stamp}_{SanitizeFileName(name)}_{hash}";
    }

    private static string BuildGroupId(WenShuDrawingIdentity drawing, WenShuDbTextContext context)
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

    private static WenShuDbTextPointSnapshot Point(double x, double y, double z)
    {
        return new WenShuDbTextPointSnapshot(
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

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}

internal readonly record struct WenShuDbTextExportResult(
    string PackageDirectory,
    int Scanned,
    int Exported,
    int ProblemCount,
    int EmptySkipped,
    int Errors);

internal readonly record struct WenShuDbTextPointSnapshot(
    [property: JsonProperty("x")] double X,
    [property: JsonProperty("y")] double Y,
    [property: JsonProperty("z")] double Z);

internal readonly record struct WenShuDbTextExtentsSnapshot(
    [property: JsonProperty("min")] WenShuDbTextPointSnapshot Min,
    [property: JsonProperty("max")] WenShuDbTextPointSnapshot Max);

internal readonly record struct WenShuDbTextGeometrySnapshot(
    [property: JsonProperty("position")] WenShuDbTextPointSnapshot Position,
    [property: JsonProperty("alignmentPoint")] WenShuDbTextPointSnapshot AlignmentPoint,
    [property: JsonProperty("normal")] WenShuDbTextPointSnapshot Normal,
    [property: JsonProperty("height")] double Height,
    [property: JsonProperty("rotation")] double Rotation,
    [property: JsonProperty("widthFactor")] double WidthFactor,
    [property: JsonProperty("oblique")] double Oblique,
    [property: JsonProperty("horizontalMode")] string HorizontalMode,
    [property: JsonProperty("verticalMode")] string VerticalMode,
    [property: JsonProperty("isMirroredInX")] bool IsMirroredInX,
    [property: JsonProperty("isMirroredInY")] bool IsMirroredInY,
    [property: JsonProperty("extents")] WenShuDbTextExtentsSnapshot? Extents);
#endif

