#if DEBUG
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.FontMapping;

namespace AFR.Services;

/// <summary>
/// DBText AI repair training-sample exporter.
/// <para>
/// This service records model-ready samples only. The built-in AI decision hook is
/// intentionally wired to a null model until a trained model is added and gated.
/// </para>
/// </summary>
internal static class DbTextAiTrainingSampleService
{
    private const string SchemaVersion = "dbtext-ai-sample-v1";
    private const string Source = "dbtext-observed-fallback";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly Dictionary<string, DrawingIdentity> DrawingIdentityCache = new(StringComparer.OrdinalIgnoreCase);

    private static StreamWriter? _writer;
    private static string? _outputPath;
    private static string? _sessionId;
    private static int _sampleCount;

    public static void BeginSession(Database db)
    {
        lock (Sync)
        {
            CloseWriterNoThrow();
            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _sampleCount = 0;

            string outputDir = Path.GetDirectoryName(typeof(DbTextAiTrainingSampleService).Assembly.Location)
                ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(outputDir);
            _outputPath = Path.Combine(outputDir, $"AFR_DbTextAITraining_{_sessionId}.jsonl");

            DiagnosticLogger.Log(
                "DBTextAI",
                $"训练样本导出已启用。Path='{_outputPath}', Drawing='{SafeFileName(db.Filename)}', Mode=diagnostic-only");
        }
    }

    public static void RecordCandidate(
        Database db,
        Transaction tr,
        DBText dbText,
        NativeDbTextProvenance provenance,
        string candidateText,
        string observedReason,
        string nativeEvidenceReason,
        string deterministicDecision,
        string deterministicReason)
    {
        try
        {
            DbTextAiTrainingSample sample = CreateSample(
                db,
                tr,
                dbText,
                provenance,
                candidateText,
                observedReason,
                nativeEvidenceReason,
                deterministicDecision,
                deterministicReason);

            DbTextAiDecision aiDecision = DbTextAiRepairAdvisor.Evaluate(sample);
            sample.AiDecision = aiDecision.Decision;
            sample.AiConfidence = aiDecision.Confidence;
            sample.AiReason = aiDecision.Reason;
            sample.AiCanAutoWrite = false;

            string json = JsonSerializer.Serialize(sample, JsonOptions);
            lock (Sync)
            {
                if (_outputPath == null)
                    BeginSession(db);

                _writer ??= new StreamWriter(
                    new FileStream(_outputPath!, FileMode.Append, FileAccess.Write, FileShare.Read),
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _writer.WriteLine(json);
                _sampleCount++;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("DBTextAI", $"训练样本记录失败 Handle={dbText.Handle}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void EndSession()
    {
        lock (Sync)
        {
            try { _writer?.Flush(); } catch { }
            DiagnosticLogger.Log(
                "DBTextAI",
                $"训练样本导出完成。Samples={_sampleCount}, Path='{_outputPath ?? "<none>"}', Mode=diagnostic-only");
        }
    }

    private static DbTextAiTrainingSample CreateSample(
        Database db,
        Transaction tr,
        DBText dbText,
        NativeDbTextProvenance provenance,
        string candidateText,
        string observedReason,
        string nativeEvidenceReason,
        string deterministicDecision,
        string deterministicReason)
    {
        string currentText = dbText.TextString ?? string.Empty;
        DrawingIdentity drawing = GetDrawingIdentity(db.Filename);
        TextStyleIdentity style = GetTextStyleIdentity(tr, dbText);

        return new DbTextAiTrainingSample
        {
            SchemaVersion = SchemaVersion,
            Source = Source,
            SessionId = _sessionId ?? string.Empty,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Label = null,
            LabelSource = "unlabeled",
            DrawingPath = drawing.Path,
            DrawingFileName = drawing.FileName,
            DrawingLength = drawing.Length,
            DrawingLastWriteUtc = drawing.LastWriteUtc,
            DrawingSha256 = drawing.Sha256,
            EntityType = "DBText",
            ObjectId = SafeObjectId(dbText.ObjectId),
            Handle = dbText.Handle.ToString(),
            Layer = Safe(() => dbText.Layer, string.Empty),
            OwnerBlockName = GetOwnerBlockName(tr, dbText),
            TextStyleName = style.Name,
            TextStyleFileName = style.FileName,
            TextStyleBigFontFileName = style.BigFontFileName,
            TextStyleTypeFace = style.TypeFace,
            CodePageId = provenance.CodePageId,
            CodePage = DwgFilerCodePageScopeHook.FormatCodePageId(provenance.CodePageId),
            ReadStringEventCount = provenance.ReadStringEvents.Length,
            CurrentText = currentText,
            CandidateText = candidateText,
            ObservedReason = observedReason,
            NativeEvidenceReason = nativeEvidenceReason,
            DeterministicDecision = deterministicDecision,
            DeterministicReason = deterministicReason,
            AsciiSkeleton = ExtractAsciiSkeleton(currentText),
            CandidateAsciiSkeleton = ExtractAsciiSkeleton(candidateText),
            CurrentStats = AnalyzeText(currentText),
            CandidateStats = AnalyzeText(candidateText)
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
                    var descriptor = style.Font;
                    typeFace = descriptor.TypeFace ?? string.Empty;
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
            // Diagnostic-only path; leave style fields empty if AutoCAD rejects a read.
        }

        return new TextStyleIdentity(string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static string GetOwnerBlockName(Transaction tr, DBText dbText)
    {
        try
        {
            if (tr.GetObject(dbText.OwnerId, OpenMode.ForRead, false, true) is BlockTableRecord owner)
                return owner.Name;
        }
        catch
        {
            // Diagnostic-only path.
        }

        return string.Empty;
    }

    private static DrawingIdentity GetDrawingIdentity(string? path)
    {
        path ??= string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return DrawingIdentity.Empty;

        lock (DrawingIdentityCache)
        {
            if (DrawingIdentityCache.TryGetValue(path, out DrawingIdentity cached))
                return cached;

            DrawingIdentity identity = BuildDrawingIdentity(path);
            DrawingIdentityCache[path] = identity;
            return identity;
        }
    }

    private static DrawingIdentity BuildDrawingIdentity(string path)
    {
        try
        {
            var info = new FileInfo(path);
            string sha256 = string.Empty;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sha = SHA256.Create();
                sha256 = Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch
            {
                sha256 = string.Empty;
            }

            return new DrawingIdentity(
                path,
                info.Name,
                info.Exists ? info.Length : 0,
                info.Exists ? info.LastWriteTimeUtc.ToString("O") : string.Empty,
                sha256);
        }
        catch
        {
            return new DrawingIdentity(path, SafeFileName(path), 0, string.Empty, string.Empty);
        }
    }

    private static DbTextAiTextStats AnalyzeText(string text)
    {
        var stats = new DbTextAiTextStats { Length = text.Length };
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x7F)
            {
                stats.Ascii++;
                if (char.IsLetter(ch))
                    stats.AsciiLetter++;
                if (char.IsDigit(ch))
                    stats.AsciiDigit++;
                if (char.IsPunctuation(ch) || char.IsSymbol(ch))
                    stats.AsciiPunctuationOrSymbol++;
                continue;
            }

            if (char.IsWhiteSpace(ch))
                stats.Whitespace++;
            else if (ch >= '\u4E00' && ch <= '\u9FFF')
                stats.CjkUnified++;
            else if (ch >= '\uE000' && ch <= '\uF8FF')
                stats.PrivateUse++;
            else if (ch >= '\u3100' && ch <= '\u312F')
                stats.Bopomofo++;
            else if ((ch >= '\u3040' && ch <= '\u30FF') || (ch >= '\u31F0' && ch <= '\u31FF'))
                stats.Kana++;
            else if (ch >= '\uAC00' && ch <= '\uD7AF')
                stats.Hangul++;
            else if (ch >= '\uFF01' && ch <= '\uFF5E')
                stats.FullwidthAscii++;
            else if (char.IsPunctuation(ch) || char.IsSymbol(ch))
                stats.NonAsciiPunctuationOrSymbol++;
            else
                stats.OtherNonAscii++;
        }

        return stats;
    }

    private static string ExtractAsciiSkeleton(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x7F)
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string SafeObjectId(ObjectId objectId)
    {
        try { return objectId.ToString(); }
        catch { return string.Empty; }
    }

    private static string SafeFileName(string? path)
    {
        try { return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path); }
        catch { return path ?? string.Empty; }
    }

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    private static void CloseWriterNoThrow()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _outputPath = null;
    }

    private readonly record struct DrawingIdentity(
        string Path,
        string FileName,
        long Length,
        string LastWriteUtc,
        string Sha256)
    {
        public static DrawingIdentity Empty { get; } = new(string.Empty, string.Empty, 0, string.Empty, string.Empty);
    }

    private readonly record struct TextStyleIdentity(
        string Name,
        string FileName,
        string BigFontFileName,
        string TypeFace);
}

internal static class DbTextAiRepairAdvisor
{
    private static IDbTextAiRepairModel Model { get; } = new NullDbTextAiRepairModel();

    public static DbTextAiDecision Evaluate(DbTextAiTrainingSample sample)
    {
        try
        {
            return Model.Evaluate(sample);
        }
        catch (Exception ex)
        {
            return new DbTextAiDecision("abstain", 0.0, "model-error " + ex.GetType().Name);
        }
    }
}

internal interface IDbTextAiRepairModel
{
    DbTextAiDecision Evaluate(DbTextAiTrainingSample sample);
}

internal sealed class NullDbTextAiRepairModel : IDbTextAiRepairModel
{
    public DbTextAiDecision Evaluate(DbTextAiTrainingSample sample)
    {
        return new DbTextAiDecision(
            "abstain",
            0.0,
            "null-model diagnostic-only; no AI auto-write");
    }
}

internal sealed record DbTextAiDecision(
    string Decision,
    double Confidence,
    string Reason);

internal sealed class DbTextAiTrainingSample
{
    public string SchemaVersion { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string TimestampUtc { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string LabelSource { get; set; } = string.Empty;
    public string DrawingPath { get; set; } = string.Empty;
    public string DrawingFileName { get; set; } = string.Empty;
    public long DrawingLength { get; set; }
    public string DrawingLastWriteUtc { get; set; } = string.Empty;
    public string DrawingSha256 { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string OwnerBlockName { get; set; } = string.Empty;
    public string TextStyleName { get; set; } = string.Empty;
    public string TextStyleFileName { get; set; } = string.Empty;
    public string TextStyleBigFontFileName { get; set; } = string.Empty;
    public string TextStyleTypeFace { get; set; } = string.Empty;
    public int CodePageId { get; set; }
    public string CodePage { get; set; } = string.Empty;
    public int ReadStringEventCount { get; set; }
    public string CurrentText { get; set; } = string.Empty;
    public string CandidateText { get; set; } = string.Empty;
    public string ObservedReason { get; set; } = string.Empty;
    public string NativeEvidenceReason { get; set; } = string.Empty;
    public string DeterministicDecision { get; set; } = string.Empty;
    public string DeterministicReason { get; set; } = string.Empty;
    public string AsciiSkeleton { get; set; } = string.Empty;
    public string CandidateAsciiSkeleton { get; set; } = string.Empty;
    public DbTextAiTextStats CurrentStats { get; set; } = new();
    public DbTextAiTextStats CandidateStats { get; set; } = new();
    public string AiDecision { get; set; } = "abstain";
    public double AiConfidence { get; set; }
    public string AiReason { get; set; } = string.Empty;
    public bool AiCanAutoWrite { get; set; }
}

internal sealed class DbTextAiTextStats
{
    public int Length { get; set; }
    public int Ascii { get; set; }
    public int AsciiLetter { get; set; }
    public int AsciiDigit { get; set; }
    public int AsciiPunctuationOrSymbol { get; set; }
    public int Whitespace { get; set; }
    public int CjkUnified { get; set; }
    public int PrivateUse { get; set; }
    public int Bopomofo { get; set; }
    public int Kana { get; set; }
    public int Hangul { get; set; }
    public int FullwidthAscii { get; set; }
    public int NonAsciiPunctuationOrSymbol { get; set; }
    public int OtherNonAscii { get; set; }
}
#endif
