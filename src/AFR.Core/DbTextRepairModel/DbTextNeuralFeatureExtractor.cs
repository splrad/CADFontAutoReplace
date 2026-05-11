using System;
using System.Globalization;
using System.Text;

namespace AFR.DbTextRepairModel;

internal static class DbTextNeuralFeatureExtractor
{
    public const int FeatureCount = 40;

    public static float[] Extract(DbTextRepairModelRecord context, string candidateText, string candidateSource)
    {
        string current = context.CurrentText ?? string.Empty;
        string candidate = candidateText ?? string.Empty;
        var features = new float[FeatureCount];

        TextStats currentStats = Analyze(current);
        TextStats candidateStats = Analyze(candidate);

        features[0] = Norm(current.Length, 64);
        features[1] = Norm(candidate.Length, 64);
        features[2] = Norm(Math.Abs(current.Length - candidate.Length), 32);
        features[3] = Bool(string.Equals(current, candidate, StringComparison.Ordinal));
        features[4] = Bool(string.IsNullOrEmpty(candidate));
        features[5] = currentStats.CjkRatio;
        features[6] = candidateStats.CjkRatio;
        features[7] = currentStats.PrivateUseRatio;
        features[8] = candidateStats.PrivateUseRatio;
        features[9] = currentStats.AsciiRatio;
        features[10] = candidateStats.AsciiRatio;
        features[11] = currentStats.DigitRatio;
        features[12] = candidateStats.DigitRatio;
        features[13] = currentStats.PunctuationRatio;
        features[14] = candidateStats.PunctuationRatio;
        features[15] = currentStats.BopomofoOrKanaRatio;
        features[16] = candidateStats.BopomofoOrKanaRatio;
        features[17] = currentStats.SymbolRatio;
        features[18] = candidateStats.SymbolRatio;
        features[19] = Bool(current.IndexOf('?') >= 0);
        features[20] = Bool(candidate.IndexOf('?') >= 0);
        features[21] = NormalizedEditDistance(current, candidate);
        features[22] = CharacterOverlap(current, candidate);
        features[23] = ContainsSource(candidateSource, "big5");
        features[24] = ContainsSource(candidateSource, "historical");
        features[25] = ContainsSource(candidateSource, "current");
        features[26] = StableHash01(context.Layer);
        features[27] = StableHash01(context.TextStyleName);
        features[28] = StableHash01(context.TextStyleBigFontFileName);
        features[29] = StableHash01(context.TextStyleFileName);
        features[30] = StableHash01(context.OwnerBlockName);
        features[31] = CadKeywordRatio(candidate);
        features[32] = CadKeywordRatio(current);
        features[33] = Bool(candidateStats.CjkRatio > currentStats.CjkRatio);
        features[34] = Bool(current.Length <= 1);
        features[35] = Bool(candidate.Length <= 1);
        features[36] = Bool(currentStats.PrivateUseRatio > 0);
        features[37] = Bool(candidateStats.PrivateUseRatio > 0);
        features[38] = Bool(candidate.IndexOf('\uFFFD') >= 0);
        features[39] = Bool(current.IndexOf('\uFFFD') >= 0);

        return features;
    }

    private static TextStats Analyze(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new TextStats();

        int cjk = 0;
        int privateUse = 0;
        int ascii = 0;
        int digit = 0;
        int punctuation = 0;
        int bopomofoOrKana = 0;
        int symbol = 0;

        foreach (char ch in text)
        {
            if (ch <= 0x7F)
                ascii++;
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
            PrivateUseRatio = privateUse / length,
            AsciiRatio = ascii / length,
            DigitRatio = digit / length,
            PunctuationRatio = punctuation / length,
            BopomofoOrKanaRatio = bopomofoOrKana / length,
            SymbolRatio = symbol / length
        };
    }

    private static float CadKeywordRatio(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        const string keywords = "水管井泵阀风压流排污喷淋消防电气设备材料表房库层标高详见安装系统";
        int count = 0;
        foreach (char ch in text)
        {
            if (keywords.IndexOf(ch) >= 0)
                count++;
        }

        return count / (float)Math.Max(1, text.Length);
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

        int union = Math.Max(left.Length, right.Length);
        return intersection / (float)Math.Max(1, union);
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

    private static float ContainsSource(string source, string token)
    {
        return Bool(!string.IsNullOrEmpty(source)
                    && source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static float Bool(bool value) => value ? 1f : 0f;

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
        public float PrivateUseRatio;
        public float AsciiRatio;
        public float DigitRatio;
        public float PunctuationRatio;
        public float BopomofoOrKanaRatio;
        public float SymbolRatio;
    }
}
