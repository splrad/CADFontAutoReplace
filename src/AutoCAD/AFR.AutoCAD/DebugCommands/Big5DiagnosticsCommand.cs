#if DEBUG
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AFR.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.DebugCommands.Big5DiagnosticsCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// Big5/GBK 编码诊断命令集合。
/// 仅在 DEBUG 构建可用，用于诊断 DBText 是否包含 Big5 / 非简体编码样本，以及自动修复后的残留情况。
/// 所有命令仅读取数据，不修改图纸。
/// </summary>
public class Big5DiagnosticsCommand
{
    private const int MaxBig5Samples = 200;

    /// <summary>
    /// AFRBIG5DIAG 命令：扫描当前图纸中的单行文字，输出编码诊断样本。
    /// </summary>
    [CommandMethod(AFR.Constants.CommandNames.Big5Diagnose)]
    public void DiagnoseBig5Text()
    {
        var log = LogService.Instance;
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            string report;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                report = BuildBig5Diagnostics(doc.Database, tr);
                tr.Commit();
            }

            DiagnosticLogger.Log("Big5诊断", report);
            log.Info("Big5 编码诊断已输出到调试日志。命令: AFRLOG");
            log.Flush();
        }
        catch (System.Exception ex)
        {
            log.Error("Big5 编码诊断失败", ex);
        }
    }

    /// <summary>
    /// AFRBIG5LEFT 命令：扫描自动修复后仍疑似残留乱码的单行文字。
    /// </summary>
    [CommandMethod(AFR.Constants.CommandNames.Big5Residual)]
    public void DiagnoseResidualBig5Text()
    {
        var log = LogService.Instance;
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            string report;
            using (doc.LockDocument())
            {
                report = DbTextEncodingRepairService.BuildResidualDiagnostics(doc.Database);
            }

            DiagnosticLogger.Log("Big5残留诊断", report);
            log.Info("Big5 残留诊断已输出到调试日志。命令: AFRLOG");
            log.Flush();
        }
        catch (System.Exception ex)
        {
            log.Error("Big5 残留诊断失败", ex);
        }
    }

    private static string BuildBig5Diagnostics(Database db, Transaction tr)
    {
        var sb = new StringBuilder(64 * 1024);
        int totalDbText = 0;
        int sampled = 0;
        int candidateCount = 0;

        sb.AppendLine("=== AFR Big5 单行文字诊断 ===");
        sb.AppendLine($"Database={db.Filename}");
        sb.AppendLine("字段: Entity|Handle|Block|Style|Main|Big|Raw|CodePoints|GBKBytesToBig5|Big5BytesToGBK|ScoreRaw|ScoreA|ScoreB|Decision");

        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId btrId in bt)
        {
            BlockTableRecord? btr = null;
            try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
            catch { continue; }

            foreach (ObjectId entId in btr)
            {
                DBText? text = null;
                try { text = tr.GetObject(entId, OpenMode.ForRead) as DBText; }
                catch { continue; }
                if (text == null) continue;

                totalDbText++;
                string raw = text.TextString ?? string.Empty;
                bool isSuspicious = ShouldSampleRawText(raw);

                string styleName = string.Empty;
                string mainFont = string.Empty;
                string bigFont = string.Empty;
                try
                {
                    var style = (TextStyleTableRecord)tr.GetObject(text.TextStyleId, OpenMode.ForRead);
                    styleName = style.Name;
                    mainFont = style.FileName ?? string.Empty;
                    bigFont = style.BigFontFileName ?? string.Empty;
                }
                catch { }

                string candidateA = TryTranscode(raw, 936, 950);
                string candidateB = TryTranscode(raw, 950, 936);
                int rawScore = ScoreChineseText(raw);
                int scoreA = ScoreChineseText(candidateA);
                int scoreB = ScoreChineseText(candidateB);
                string decision = DecideEncoding(rawScore, scoreA, scoreB);
                if (decision != "None") candidateCount++;
                if (!isSuspicious && decision == "None") continue;

                sampled++;
                sb.AppendLine(string.Join("|",
                    "DBText",
                    text.Handle.ToString(),
                    EscapeDiag(btr.Name),
                    EscapeDiag(styleName),
                    EscapeDiag(mainFont),
                    EscapeDiag(bigFont),
                    EscapeDiag(raw),
                    EscapeDiag(ToCodePoints(raw)),
                    EscapeDiag(candidateA),
                    EscapeDiag(candidateB),
                    rawScore.ToString(),
                    scoreA.ToString(),
                    scoreB.ToString(),
                    decision));
            }

            if (sampled >= MaxBig5Samples) break;
        }

        sb.Insert(0, $"TotalDBText={totalDbText}, Sampled={sampled}, Candidate={candidateCount}\n");
        return sb.ToString();
    }

    private static bool ShouldSampleRawText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.Length < 2) return false;
        if (raw.All(static c => c < 128 && (char.IsDigit(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)))) return false;
        return raw.Any(static c => c > 127) || raw.Any(static c => c == '?' || c == '�');
    }

    private static string TryTranscode(string text, int sourceCodePage, int targetCodePage)
    {
        try
        {
            // .NET 8 的非 Unicode 代码页需要 System.Text.Encoding.CodePages 包。
            // 为保持单 DLL 分发，诊断命令通过反射尝试注册；若运行时未携带该包则安全降级为空候选。
            TryRegisterCodePagesProvider();

            var source = Encoding.GetEncoding(
                sourceCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            var target = Encoding.GetEncoding(
                targetCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            byte[] bytes = source.GetBytes(text);
            return target.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryRegisterCodePagesProvider()
    {
        try
        {
            var providerType = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
            var instance = providerType?.GetProperty("Instance")?.GetValue(null) as EncodingProvider;
            if (instance != null)
                Encoding.RegisterProvider(instance);
        }
        catch
        {
        }
    }

    private static int ScoreChineseText(string text)
    {
        if (string.IsNullOrEmpty(text)) return -1000;

        int score = 0;
        foreach (char c in text)
        {
            if (IsCjk(c)) score += 5;
            else if (c is '�' or '?') score -= 8;
            else if (char.IsControl(c)) score -= 10;
            else if (c > 127) score -= 1;
        }

        if (Regex.IsMatch(text, @"[\u4E00-\u9FFF]{2,}")) score += 10;
        return score;
    }

    private static string DecideEncoding(int rawScore, int scoreA, int scoreB)
    {
        int best = Math.Max(scoreA, scoreB);
        if (best - rawScore < 15) return "None";
        return scoreA >= scoreB ? "GBKBytesToBig5" : "Big5BytesToGBK";
    }

    private static bool IsCjk(char c)
        => c >= '\u4E00' && c <= '\u9FFF';

    private static string ToCodePoints(string text)
        => string.Join(" ", text.Select(static c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));

    private static string EscapeDiag(string value)
        => (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("|", "¦");
}
#endif
