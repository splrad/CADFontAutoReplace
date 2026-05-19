using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairFeatureExtractor
{
    public const int FeatureCount = 101;

    public static float[] Extract(GlyphCoreTextRepairContext context, GlyphCoreTextRepairCandidate candidate)
    {
        string current = context.CurrentText ?? string.Empty;
        string candidateText = candidate.Text ?? string.Empty;
        var features = new float[FeatureCount];

        TextStats currentStats = Analyze(current);
        TextStats candidateStats = Analyze(candidateText);

        features[0] = Norm(current.Length, 64);
        features[1] = Norm(candidateText.Length, 64);
        features[2] = Norm(Math.Abs(current.Length - candidateText.Length), 32);
        features[3] = Bool(string.Equals(current, candidateText, StringComparison.Ordinal));
        features[4] = Bool(string.IsNullOrEmpty(candidateText));
        features[5] = currentStats.CjkRatio;
        features[6] = candidateStats.CjkRatio;
        features[7] = currentStats.AsciiRatio;
        features[8] = candidateStats.AsciiRatio;
        features[9] = currentStats.ControlRatio;
        features[10] = candidateStats.ControlRatio;
        features[11] = currentStats.ReplacementRatio;
        features[12] = candidateStats.ReplacementRatio;
        features[13] = currentStats.PrivateUseRatio;
        features[14] = candidateStats.PrivateUseRatio;
        features[15] = currentStats.DigitRatio;
        features[16] = candidateStats.DigitRatio;
        features[17] = currentStats.PunctuationRatio;
        features[18] = candidateStats.PunctuationRatio;
        features[19] = currentStats.SymbolRatio;
        features[20] = candidateStats.SymbolRatio;
        features[21] = currentStats.BopomofoOrKanaRatio;
        features[22] = candidateStats.BopomofoOrKanaRatio;
        features[23] = NormalizedEditDistance(current, candidateText);
        features[24] = CharacterOverlap(current, candidateText);
        features[25] = Bool(candidate.IsRoundTrip);
        features[26] = SourceFrom(candidate.Source, "big5");
        features[27] = SourceFrom(candidate.Source, "gbk");
        features[28] = ContainsSource(candidate.Source, "utf8");
        features[29] = ContainsSource(candidate.Source, "current");
        features[30] = ContainsSource(candidate.Source, "safe");
        features[31] = CadKeywordRatio(current);
        features[32] = CadKeywordRatio(candidateText);
        features[33] = Bool(candidateStats.CjkRatio > currentStats.CjkRatio);
        features[34] = Bool(candidateStats.ControlRatio > 0);
        features[35] = Bool(candidateStats.ReplacementRatio > 0);
        features[36] = Bool(candidateStats.PrivateUseRatio > 0);
        features[37] = Bool(HasSuspiciousUnicode(candidateText));
        features[38] = Bool(HasSuspiciousUnicode(current));
        features[39] = Bool(context.IsFromExternalReference);
        features[40] = StableHash01(context.Layer);
        features[41] = StableHash01(context.OwnerBlockName);
        features[42] = StableHash01(context.TextStyleName);
        // Font file names and typefaces vary by AutoCAD version and user workstation.
        // Keep the feature slots for schema compatibility, but do not let the model
        // learn repair decisions from environment-specific font identity.
        features[43] = 0f;
        features[44] = 0f;
        features[45] = 0f;
        features[46] = Bool(IsKnownCadTextStyle(context.TextStyleName));
        features[47] = 0f;
        features[48] = 0f;
        features[49] = Bool(candidateText.Length <= 1);
        features[50] = Bool(current.Length <= 1);
        features[51] = Bool(LengthRisk(current, candidateText));
        features[52] = Bool(candidateStats.CjkRatio < currentStats.CjkRatio && currentStats.CjkRatio > 0.2f);
        features[53] = Bool(candidateStats.AsciiRatio > 0.9f && currentStats.AsciiRatio < 0.5f);
        features[54] = Bool(IsMostlySymbols(candidateStats));
        features[55] = Bool(IsMostlySymbols(currentStats));

        // f56–f61: v2 新增特征
        features[56] = CandidateLenRatio(current.Length, candidateText.Length);
        features[57] = Norm(CjkCount(current), 8);
        features[58] = Norm(CjkCount(candidateText), 8);
        features[59] = 0f;
        features[60] = 0f;
        features[61] = CandidateSourceConvergence(candidate.Source);

        // f62-f77: v3 Hook evidence features. Text appearance is still available to the model,
        // but the strong trigger comes from native decode evidence recorded before DBText repair.
        TextStats rippleStats = Analyze(context.RippleContextText);
        features[62] = Bool(context.HasNativeDecodeEvidence);
        features[63] = Bool(context.NativeDecodeFamilyMismatch);
        features[64] = Bool(IsEvidenceScope(context, "object"));
        features[65] = Bool(IsEvidenceScope(context, "cluster"));
        features[66] = Bool(IsEvidenceScope(context, "ripple"));
        features[67] = Bool(IsCodePageFamily(context.NativeDecodeSourceCodePageFamily, "big5"));
        features[68] = Bool(IsCodePageFamily(context.NativeDecodeSourceCodePageFamily, "gbk"));
        features[69] = Bool(IsCodePageFamily(context.NativeDecodeAppliedCodePageFamily, "big5"));
        features[70] = Bool(IsCodePageFamily(context.NativeDecodeAppliedCodePageFamily, "gbk"));
        features[71] = Clamp01(context.NativeDecodeObjectCorrelation);
        features[72] = Clamp01(context.NativeDecodeClusterCorrelation);
        features[73] = Bool(IsHookHitType(context.NativeDecodeHookHitType, "dbtext"));
        features[74] = 0f;
        features[75] = Norm(context.RippleSeedCount, 8);
        features[76] = rippleStats.CjkRatio;
        features[77] = Bool(IsEvidenceAlignedCandidate(context, candidate.Source));

        // f78-f97: v4 engineering semantics, raw Hook candidate evidence, and ripple quality.
        features[78] = SimplifiedEngineeringChineseRatio(current);
        features[79] = SimplifiedEngineeringChineseRatio(candidateText);
        features[80] = Bool(SimplifiedEngineeringChineseRatio(candidateText) > SimplifiedEngineeringChineseRatio(current));
        features[81] = TraditionalOrRareCjkRatio(current);
        features[82] = TraditionalOrRareCjkRatio(candidateText);
        features[83] = Bool(TraditionalOrRareCjkRatio(candidateText) < TraditionalOrRareCjkRatio(current));
        features[84] = EngineeringKeywordRatio(context.Layer);
        features[85] = LayerCandidateKeywordOverlap(context.Layer, candidateText);
        features[86] = Bool(PreservesAsciiTokens(current, candidateText));
        features[87] = EngineeringSymbolPreservation(current, candidateText);
        features[88] = Norm(AsciiTokenCount(current), 8);
        features[89] = Norm(AsciiTokenCount(candidateText), 8);
        features[90] = Norm(EngineeringSymbolCount(current), 8);
        features[91] = Norm(EngineeringSymbolCount(candidateText), 8);
        features[92] = Bool(context.HasHookRawDecodeEvidence && context.HookRawPayloadLength > 0);
        features[93] = Bool(IsHookRawPreferredCandidate(context, candidateText, candidate.Source));
        features[94] = Bool(context.HookRawRoundTrip);
        features[95] = Clamp01(context.HookRawConfidence);
        features[96] = Clamp01(context.RippleSeedQuality);
        features[97] = Clamp01(context.RippleDistanceRatio);
        features[98] = StableHash01(current);
        features[99] = StableHash01(candidateText);
        features[100] = StableHash01(current + "\0" + candidateText + "\0" + candidate.Source);

        return features;
    }

    public static bool HasUnsafeText(string text)
    {
        TextStats stats = Analyze(text);
        return stats.ControlRatio > 0
               || stats.ReplacementRatio > 0
               || HasSuspiciousUnicode(text);
    }

    public static bool HasUnsafeRepairCandidateText(string text)
    {
        TextStats stats = Analyze(text);
        return stats.ControlRatio > 0
               || stats.ReplacementRatio > 0
               || HasLeadingPrivateUsePlaceholder(text)
               || HasDisallowedRepairCandidateUnicode(text)
               || HasSuspiciousUnicode(text);
    }

    private static bool HasLeadingPrivateUsePlaceholder(string text)
    {
        if (string.IsNullOrEmpty(text) || !IsPrivateUse(text[0]))
            return false;

        int prefixLength = 0;
        while (prefixLength < text.Length && prefixLength < 4 && IsPrivateUse(text[prefixLength]))
            prefixLength++;

        return prefixLength > 0 && prefixLength < text.Length;
    }

    private static bool HasDisallowedRepairCandidateUnicode(string text)
    {
        foreach (char ch in text ?? string.Empty)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (char.IsControl(ch)
                || char.IsSurrogate(ch)
                || ch == '\uFFFD'
                || IsPrivateUse(ch)
                || IsBopomofoOrKana(ch)
                || category == UnicodeCategory.OtherNotAssigned)
                return true;

            if (IsNonEngineeringSymbol(ch, category))
                return true;
        }

        return false;
    }

    private static bool IsNonEngineeringSymbol(char ch, UnicodeCategory category)
    {
        if (category != UnicodeCategory.MathSymbol
            && category != UnicodeCategory.CurrencySymbol
            && category != UnicodeCategory.ModifierSymbol
            && category != UnicodeCategory.OtherSymbol)
            return false;

        const string allowed = "+-()./×xXΦφ%#@=<>＜＞≤≥±≈≠~～°℃㎡㎥³ⅡⅢⅣⅤ‰′″¨";
        return allowed.IndexOf(ch) < 0;
    }

    private static TextStats Analyze(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new TextStats();

        int cjk = 0;
        int ascii = 0;
        int control = 0;
        int replacement = 0;
        int privateUse = 0;
        int digit = 0;
        int punctuation = 0;
        int symbol = 0;
        int bopomofoOrKana = 0;

        foreach (char ch in text)
        {
            if (ch <= 0x7F)
                ascii++;
            if (char.IsControl(ch))
                control++;
            if (ch == '\uFFFD')
                replacement++;
            if (char.IsDigit(ch))
                digit++;
            if (IsCjk(ch))
                cjk++;
            if (IsPrivateUse(ch))
                privateUse++;
            if (IsBopomofoOrKana(ch))
                bopomofoOrKana++;

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.OtherPunctuation
                || category == UnicodeCategory.ConnectorPunctuation
                || category == UnicodeCategory.DashPunctuation
                || category == UnicodeCategory.OpenPunctuation
                || category == UnicodeCategory.ClosePunctuation
                || category == UnicodeCategory.InitialQuotePunctuation
                || category == UnicodeCategory.FinalQuotePunctuation)
                punctuation++;
            if (category == UnicodeCategory.MathSymbol
                || category == UnicodeCategory.CurrencySymbol
                || category == UnicodeCategory.ModifierSymbol
                || category == UnicodeCategory.OtherSymbol)
                symbol++;
        }

        float length = Math.Max(1, text.Length);
        return new TextStats
        {
            CjkRatio = cjk / length,
            AsciiRatio = ascii / length,
            ControlRatio = control / length,
            ReplacementRatio = replacement / length,
            PrivateUseRatio = privateUse / length,
            DigitRatio = digit / length,
            PunctuationRatio = punctuation / length,
            SymbolRatio = symbol / length,
            BopomofoOrKanaRatio = bopomofoOrKana / length
        };
    }

    private static float CadKeywordRatio(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        const string keywords = "水管井泵阀风压流排污喷淋消防电气设备材料表房库层标高详见安装系统屋顶支架压力自动";
        int count = 0;
        foreach (char ch in text)
        {
            if (keywords.IndexOf(ch) >= 0)
                count++;
        }

        return count / (float)Math.Max(1, text.Length);
    }

    private static float SimplifiedEngineeringChineseRatio(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int count = 0;
        int cjk = 0;
        foreach (char ch in text)
        {
            if (!IsCjk(ch))
                continue;

            cjk++;
            if (IsSimplifiedEngineeringChar(ch))
                count++;
        }

        return count / (float)Math.Max(1, cjk);
    }

    private static float TraditionalOrRareCjkRatio(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int count = 0;
        foreach (char ch in text)
        {
            if (IsTraditionalOrRareCjk(ch))
                count++;
        }

        return count / (float)Math.Max(1, text.Length);
    }

    private static float EngineeringKeywordRatio(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        string normalized = text.ToUpperInvariant();
        string[] tokens =
        {
            "WATER", "DRAIN", "PIPE", "FIRE", "HVAC", "ELEC", "TEXT", "DIM",
            "给水", "排水", "消防", "喷淋", "电气", "风管", "暖通", "结构", "建筑", "标注",
            "水", "管", "阀", "泵", "风", "电", "层", "井", "标高"
        };

        int hits = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (normalized.IndexOf(tokens[i].ToUpperInvariant(), StringComparison.OrdinalIgnoreCase) >= 0)
                hits++;
        }

        return Norm(hits, 6);
    }

    private static float LayerCandidateKeywordOverlap(string layer, string candidate)
    {
        HashSet<char> layerChars = EngineeringChars(layer);
        if (layerChars.Count == 0 || string.IsNullOrEmpty(candidate))
            return 0;

        int hits = 0;
        foreach (char ch in candidate)
        {
            if (layerChars.Contains(ch))
                hits++;
        }

        return hits / (float)Math.Max(1, layerChars.Count);
    }

    private static bool PreservesAsciiTokens(string current, string candidate)
    {
        List<string> tokens = ExtractAsciiTokens(current);
        if (tokens.Count == 0)
            return true;

        int preserved = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if ((candidate ?? string.Empty).IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                preserved++;
        }

        return preserved >= Math.Max(1, tokens.Count - 1);
    }

    private static float EngineeringSymbolPreservation(string current, string candidate)
    {
        int total = 0;
        int preserved = 0;
        foreach (char ch in current ?? string.Empty)
        {
            if (!IsEngineeringSymbol(ch))
                continue;

            total++;
            if ((candidate ?? string.Empty).IndexOf(ch) >= 0)
                preserved++;
        }

        if (total == 0)
            return 1f;

        return preserved / (float)total;
    }

    private static int AsciiTokenCount(string text) => ExtractAsciiTokens(text).Count;

    private static int EngineeringSymbolCount(string text)
    {
        int count = 0;
        foreach (char ch in text ?? string.Empty)
        {
            if (IsEngineeringSymbol(ch))
                count++;
        }

        return count;
    }

    private static bool IsHookRawPreferredCandidate(
        GlyphCoreTextRepairContext context,
        string candidateText,
        string candidateSource)
    {
        if (!context.HasHookRawDecodeEvidence)
            return false;

        if (ContainsToken(candidateSource, "hook-raw-stream"))
            return true;

        return !string.IsNullOrWhiteSpace(context.HookPreferredDecodedText)
               && string.Equals(context.HookPreferredDecodedText, candidateText, StringComparison.Ordinal);
    }

    private static HashSet<char> EngineeringChars(string text)
    {
        const string keywords = "水管井泵阀风压流排污喷淋消防电气设备材料表房库层标高详见安装系统屋顶支架压力自动给排暖通建筑结构标注";
        var result = new HashSet<char>();
        foreach (char ch in text ?? string.Empty)
        {
            if (keywords.IndexOf(ch) >= 0)
                result.Add(ch);
        }

        return result;
    }

    private static List<string> ExtractAsciiTokens(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (char ch in text ?? string.Empty)
        {
            if (IsAsciiTokenChar(ch))
            {
                builder.Append(ch);
                continue;
            }

            FlushToken(tokens, builder);
        }

        FlushToken(tokens, builder);
        return tokens;
    }

    private static void FlushToken(List<string> tokens, StringBuilder builder)
    {
        if (builder.Length >= 2)
            tokens.Add(builder.ToString());
        builder.Clear();
    }

    private static bool IsAsciiTokenChar(char ch)
    {
        return (ch >= 'A' && ch <= 'Z')
               || (ch >= 'a' && ch <= 'z')
               || (ch >= '0' && ch <= '9')
               || ch == '+'
               || ch == '-'
               || ch == '.'
               || ch == '/'
               || ch == '('
               || ch == ')';
    }

    private static bool IsEngineeringSymbol(char ch)
    {
        const string symbols = "+-()./×xXΦφ%#@=<>≤≥±°";
        return symbols.IndexOf(ch) >= 0;
    }

    private static bool IsSimplifiedEngineeringChar(char ch)
    {
        const string simplified = "检宽顶图层风阀喷淋电气设备材料库给压流排污消防标高详见安装系统屋顶支架自动泵管水井房";
        return simplified.IndexOf(ch) >= 0 || CadKeywordRatio(ch.ToString()) > 0;
    }

    private static bool IsTraditionalOrRareCjk(char ch)
    {
        const string traditional = "檢寬頂圖層風閥噴電氣設備給壓詳見築標號號體體臺臺";
        if (traditional.IndexOf(ch) >= 0)
            return true;

        return IsCjk(ch) && !IsSimplifiedEngineeringChar(ch) && !char.IsLetterOrDigit(ch);
    }

    private static float NormalizedEditDistance(string left, string right)
    {
        int max = Math.Max(left?.Length ?? 0, right?.Length ?? 0);
        if (max == 0)
            return 0;

        return Math.Min(1f, Levenshtein(left ?? string.Empty, right ?? string.Empty) / (float)max);
    }

    private static int Levenshtein(string left, string right)
    {
        int n = left.Length;
        int m = right.Length;
        var previous = new int[m + 1];
        var current = new int[m + 1];

        for (int j = 0; j <= m; j++)
            previous[j] = j;

        for (int i = 1; i <= n; i++)
        {
            current[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            int[] temp = previous;
            previous = current;
            current = temp;
        }

        return previous[m];
    }

    private static float CharacterOverlap(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            return 0;

        int intersection = 0;
        foreach (char ch in left)
        {
            if (right.IndexOf(ch) >= 0)
                intersection++;
        }

        return intersection / (float)Math.Max(1, Math.Max(left.Length, right.Length));
    }

    private static float StableHash01(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        unchecked
        {
            uint hash = 2166136261;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619;
            }

            return (hash & 0xFFFF) / 65535f;
        }
    }

    private static bool HasSuspiciousUnicode(string text)
    {
        foreach (char ch in text ?? string.Empty)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.Surrogate
                || category == UnicodeCategory.OtherNotAssigned)
                return true;
        }

        return false;
    }

    private static bool LengthRisk(string current, string candidate)
    {
        int currentLength = current?.Length ?? 0;
        int candidateLength = candidate?.Length ?? 0;
        if (currentLength == 0 || candidateLength == 0)
            return true;

        return candidateLength > currentLength * 4 || currentLength > candidateLength * 4;
    }

    private static bool IsMostlySymbols(TextStats stats)
    {
        return stats.SymbolRatio + stats.PunctuationRatio > 0.75f;
    }

    private static bool IsKnownCadTextStyle(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        return text.IndexOf("HZTXT", StringComparison.OrdinalIgnoreCase) >= 0
               || text.IndexOf("TXT", StringComparison.OrdinalIgnoreCase) >= 0
               || text.IndexOf("TEXT", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static float CandidateLenRatio(int currentLen, int candidateLen)
    {
        if (currentLen == 0)
            return 1f;
        return Math.Min(1f, (candidateLen / (float)currentLen) / 4f);
    }

    private static int CjkCount(string text)
    {
        int count = 0;
        foreach (char ch in text ?? string.Empty)
        {
            if (IsCjk(ch))
                count++;
        }
        return count;
    }

    private static float CandidateSourceConvergence(string source)
    {
        if (string.IsNullOrEmpty(source))
            return 0f;
        int plusCount = 0;
        foreach (char ch in source)
        {
            if (ch == '+')
                plusCount++;
        }
        return Norm(1 + plusCount, 3);
    }

    private static bool IsEvidenceAlignedCandidate(GlyphCoreTextRepairContext context, string candidateSource)
    {
        if (!context.NativeDecodeFamilyMismatch || string.IsNullOrEmpty(candidateSource))
            return false;

        string sourceFamily = context.NativeDecodeSourceCodePageFamily;
        string appliedFamily = context.NativeDecodeAppliedCodePageFamily;
        if (IsCodePageFamily(sourceFamily, "big5") && IsCodePageFamily(appliedFamily, "gbk"))
            return ContainsToken(candidateSource, "big5-carrier-to-gbk");
        if (IsCodePageFamily(sourceFamily, "gbk") && IsCodePageFamily(appliedFamily, "big5"))
            return ContainsToken(candidateSource, "gbk-carrier-to-big5");
        if (IsCodePageFamily(sourceFamily, "utf8") && IsCodePageFamily(appliedFamily, "gbk"))
            return ContainsToken(candidateSource, "utf8-carrier-to-gbk");
        if (IsCodePageFamily(sourceFamily, "gbk") && IsCodePageFamily(appliedFamily, "utf8"))
            return ContainsToken(candidateSource, "gbk-carrier-to-utf8");

        return false;
    }

    private static bool IsEvidenceScope(GlyphCoreTextRepairContext context, string scope)
        => ContainsToken(context.NativeDecodeEvidenceScope, scope);

    private static bool IsCodePageFamily(string value, string family)
        => ContainsToken(value, family);

    private static bool IsHookHitType(string value, string token)
        => ContainsToken(value, token);

    private static bool ContainsToken(string value, string token)
    {
        return !string.IsNullOrEmpty(value)
               && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static float ContainsSource(string source, string token)
    {
        return Bool(!string.IsNullOrEmpty(source)
                    && source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static float SourceFrom(string source, string family)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(family))
            return 0f;

        string prefix = family + "-carrier-to-";
        string infix = "+" + prefix;
        return Bool(source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || source.IndexOf(infix, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static float Bool(bool value) => value ? 1f : 0f;

    private static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));

    private static float Norm(int value, int scale) => Math.Min(1f, value / (float)Math.Max(1, scale));

    private static bool IsCjk(char ch)
    {
        return (ch >= '\u3400' && ch <= '\u4DBF')
               || (ch >= '\u4E00' && ch <= '\u9FFF')
               || (ch >= '\uF900' && ch <= '\uFAFF');
    }

    private static bool IsPrivateUse(char ch)
    {
        return ch >= '\uE000' && ch <= '\uF8FF';
    }

    private static bool IsBopomofoOrKana(char ch)
    {
        return (ch >= '\u3040' && ch <= '\u30FF')
               || (ch >= '\u3100' && ch <= '\u312F');
    }

    private struct TextStats
    {
        public float CjkRatio;
        public float AsciiRatio;
        public float ControlRatio;
        public float ReplacementRatio;
        public float PrivateUseRatio;
        public float DigitRatio;
        public float PunctuationRatio;
        public float SymbolRatio;
        public float BopomofoOrKanaRatio;
    }
}

