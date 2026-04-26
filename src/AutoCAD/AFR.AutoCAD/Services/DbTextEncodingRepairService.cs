using Autodesk.AutoCAD.DatabaseServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AFR.Services;

/// <summary>
/// 单行文字编码修复服务。
/// <para>
/// 用于修复旧版 DWG 中因代码页错配导致的 DBText 乱码。该服务只处理单行文字，
/// 在高置信度判断为 Big5 字节被误解码时，将文本恢复为简体中文。
/// </para>
/// </summary>
internal static class DbTextEncodingRepairService
{
    private const int MinScoreDelta = 15;
    private const int MinCandidateCjkCount = 2;
    private static bool _codePagesProviderRegistered;

    /// <summary>
    /// 扫描并修复当前数据库中的单行文字编码乱码。
    /// <para>
    /// 仅对含 SHX 大字体的文字样式进行处理，并且只采用 Big5BytesToGBK 方向，避免影响正常中文文本。
    /// </para>
    /// </summary>
    /// <param name="db">需要扫描的 AutoCAD 数据库。</param>
    /// <returns>成功修复的 DBText 数量。</returns>
    public static int Repair(Database db)
    {
        int repairedCount = 0;

        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId btrId in bt)
        {
            BlockTableRecord? btr = null;
            try
            {
                btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            }
            catch
            {
                continue;
            }

            foreach (ObjectId entId in btr)
            {
                DBText? text = null;
                try
                {
                    text = tr.GetObject(entId, OpenMode.ForRead) as DBText;
                }
                catch
                {
                    continue;
                }

                if (text == null) continue;

                if (TryRepairDbText(text, tr))
                    repairedCount++;
            }
        }

        tr.Commit();
        return repairedCount;
    }

    private static bool TryRepairDbText(DBText text, Transaction tr)
    {
        TextStyleTableRecord? style = null;
        try
        {
            style = (TextStyleTableRecord)tr.GetObject(text.TextStyleId, OpenMode.ForRead);
        }
        catch
        {
            return false;
        }

        if (!IsCandidateStyle(style)) return false;

        string raw = text.TextString ?? string.Empty;
        if (!TryRepairText(raw, out string repaired, out string reason)) return false;

        try
        {
            text.UpgradeOpen();
            text.TextString = repaired;

            DiagnosticLogger.Log(
                "DBText编码",
                $"Handle={text.Handle} Style='{style.Name}' '{EscapeForLog(raw)}' → '{EscapeForLog(repaired)}' 原因={reason}");

            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"DBText编码修复失败 Handle={text.Handle}", ex);
            return false;
        }
    }

    /// <summary>
    /// 扫描当前数据库中仍疑似乱码的单行文字。
    /// <para>
    /// 该方法仅用于诊断：不修改图纸，用于找出自动修复后仍可能残留的 DBText。
    /// </para>
    /// </summary>
    /// <param name="db">需要扫描的 AutoCAD 数据库。</param>
    /// <param name="maxSamples">最多输出的样本数量。</param>
    /// <returns>诊断文本。</returns>
    public static string BuildResidualDiagnostics(Database db, int maxSamples = 200)
    {
        var sb = new StringBuilder(32 * 1024);
        int total = 0;
        int residual = 0;

        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId btrId in bt)
        {
            BlockTableRecord? btr = null;
            try
            {
                btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            }
            catch
            {
                continue;
            }

            foreach (ObjectId entId in btr)
            {
                DBText? text = null;
                try
                {
                    text = tr.GetObject(entId, OpenMode.ForRead) as DBText;
                }
                catch
                {
                    continue;
                }

                if (text == null) continue;

                total++;
                TextStyleTableRecord? style = null;
                try
                {
                    style = (TextStyleTableRecord)tr.GetObject(text.TextStyleId, OpenMode.ForRead);
                }
                catch
                {
                    continue;
                }

                string raw = text.TextString ?? string.Empty;
                if (!IsCandidateStyle(style) || !LooksResidualMojibake(raw)) continue;

                string candidate = TryTranscode(raw, 950, 936);
                residual++;
                if (residual <= maxSamples)
                {
                    sb.AppendLine(string.Join("|",
                        "DBText",
                        text.Handle.ToString(),
                        EscapeForLog(btr.Name),
                        EscapeForLog(style.Name),
                        EscapeForLog(style.FileName ?? string.Empty),
                        EscapeForLog(style.BigFontFileName ?? string.Empty),
                        EscapeForLog(raw),
                        EscapeForLog(candidate),
                        HasKnownCjkMojibakeSignal(raw).ToString(),
                        HasReadableChineseSignal(candidate).ToString()));
                }
            }
        }

        tr.Commit();
        sb.Insert(0, $"TotalDBText={total}, Residual={residual}\n字段: Entity|Handle|Block|Style|Main|Big|Raw|Big5BytesToGBK|MojibakeSignal|ReadableSignal\n");
        return sb.ToString();
    }

    private static bool LooksResidualMojibake(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;
        if (ContainsPrivateUseOrBopomofo(text)) return true;
        if (HasKnownCjkMojibakeSignal(text)) return true;

        string candidate = TryTranscode(text, 950, 936);
        return !string.IsNullOrWhiteSpace(candidate)
            && candidate != text
            && HasReadableChineseSignal(candidate);
    }

    private static bool IsCandidateStyle(TextStyleTableRecord style)
    {
        if (style.IsShapeFile) return false;

        string bigFont = style.BigFontFileName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(bigFont)) return false;
        if (!bigFont.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            && !IsKnownChineseBigFont(bigFont))
            return false;

        string name = style.Name ?? string.Empty;
        string mainFont = style.FileName ?? string.Empty;
        return HasKnownChineseStyleSignal(name)
            || HasKnownChineseStyleSignal(mainFont)
            || HasKnownChineseStyleSignal(bigFont);
    }

    private static bool HasKnownChineseStyleSignal(string value)
        => value.IndexOf("HZ", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("HT", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("CH", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("TSSD", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("MING", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("ZDN", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsKnownChineseBigFont(string value)
        => value.IndexOf("HZ", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("CH", StringComparison.OrdinalIgnoreCase) >= 0
        || value.IndexOf("TSSD", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TryRepairText(string raw, out string repaired, out string reason)
    {
        repaired = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 2) return false;

        string candidate = TryTranscode(raw, 950, 936);
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (candidate == raw) return false;

        bool hasStrongGarbledSignal = HasGarbledSignal(raw);
        bool hasCjkMojibakeSignal = HasKnownCjkMojibakeSignal(raw) && HasReadableChineseSignal(candidate);
        if (!hasStrongGarbledSignal && !hasCjkMojibakeSignal) return false;

        int rawScore = ScoreChineseText(raw);
        int candidateScore = ScoreChineseText(candidate);
        int candidateCjkCount = CountCjk(candidate);
        int rawCjkCount = CountCjk(raw);

        if (hasStrongGarbledSignal && candidateScore - rawScore < MinScoreDelta) return false;
        if (candidateCjkCount < MinCandidateCjkCount) return false;
        if (candidateCjkCount <= rawCjkCount && !ContainsPrivateUseOrBopomofo(raw) && !hasCjkMojibakeSignal) return false;

        repaired = candidate;
        reason = $"Big5BytesToGBK Score {rawScore}->{candidateScore} CJK {rawCjkCount}->{candidateCjkCount}";
        return true;
    }

    private static bool HasKnownCjkMojibakeSignal(string text)
    {
        const string mojibakeChars = "奪阨扢掘齬蚚腔華婓眕跤弅俋厙嘎殤葩蹋蕾騵脹撰囀窒僱砆獗囥媼耋芃援堤滅葛揭眒涾儂砦莉汜徹講帤斛剕迵翋賦凳蟀諉潰党韌遵詢階善褽菁";
        int count = 0;
        foreach (char c in text)
        {
            if (mojibakeChars.IndexOf(c) >= 0)
                count++;
        }

        return count >= 2;
    }

    private static bool HasReadableChineseSignal(string text)
    {
        string[] commonWords =
        [
            "给水", "排水", "详见", "平面", "强电", "弱电", "气体", "透气", "管道", "设备",
            "连接", "采用", "施工", "内部", "外墙", "楼板", "基础", "系统", "设置", "安装"
        ];

        for (int i = 0; i < commonWords.Length; i++)
        {
            if (text.IndexOf(commonWords[i], StringComparison.Ordinal) >= 0)
                return true;
        }

        const string commonChars = "的一是在有和不为以用水电气管施详见内外部平面强弱连接采用设备系统设置安装";
        int count = 0;
        foreach (char c in text)
        {
            if (commonChars.IndexOf(c) >= 0)
                count++;
        }

        return count >= 3;
    }

    private static bool HasGarbledSignal(string text)
    {
        if (ContainsPrivateUseOrBopomofo(text)) return true;

        int suspiciousCount = 0;
        foreach (char c in text)
        {
            if (c >= '\u3100' && c <= '\u312F') suspiciousCount++;
            else if (c >= '\uFE30' && c <= '\uFE6F') suspiciousCount++;
            else if (c > 127 && !IsCjk(c)) suspiciousCount++;
        }

        return suspiciousCount >= 2;
    }

    private static bool ContainsPrivateUseOrBopomofo(string text)
        => text.Any(static c => c >= '\uE000' && c <= '\uF8FF')
        || text.Any(static c => c >= '\u3100' && c <= '\u312F')
        || text.Any(static c => c >= '\uFE30' && c <= '\uFE6F');

    private static string TryTranscode(string text, int sourceCodePage, int targetCodePage)
    {
        try
        {
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
        if (_codePagesProviderRegistered) return;

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
        finally
        {
            _codePagesProviderRegistered = true;
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

    private static int CountCjk(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (IsCjk(c)) count++;
        }

        return count;
    }

    private static bool IsCjk(char c)
        => c >= '\u4E00' && c <= '\u9FFF';

    private static string EscapeForLog(string value)
        => (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
}
