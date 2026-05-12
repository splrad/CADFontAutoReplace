using System;
using System.Collections.Generic;
using System.Globalization;

namespace AFR.DbTextAI;

internal sealed class DbTextAiProblemDetection
{
    private DbTextAiProblemDetection(bool hasProblem, string reason, IReadOnlyList<DbTextAiCandidate> candidates)
    {
        HasProblem = hasProblem;
        Reason = reason;
        Candidates = candidates;
    }

    public bool HasProblem { get; }
    public string Reason { get; }
    public IReadOnlyList<DbTextAiCandidate> Candidates { get; }

    public static DbTextAiProblemDetection NoProblem(IReadOnlyList<DbTextAiCandidate> candidates) =>
        new(false, "no-suspicious-dbtext", candidates);

    public static DbTextAiProblemDetection Problem(string reason, IReadOnlyList<DbTextAiCandidate> candidates) =>
        new(true, reason, candidates);
}

internal static class DbTextAiProblemDetector
{
    public static DbTextAiProblemDetection Detect(DbTextAiContext context)
    {
        string current = context.CurrentText ?? string.Empty;
        IReadOnlyList<DbTextAiCandidate> candidates = DbTextAiCandidateGenerator.BuildCandidates(current);
        if (string.IsNullOrWhiteSpace(current))
            return DbTextAiProblemDetection.NoProblem(candidates);

        if (DbTextAiFeatureExtractor.HasUnsafeText(current))
            return DbTextAiProblemDetection.Problem("unsafe-current-text", candidates);

        TextStats currentStats = Analyze(current);
        if (LooksLikeMojibake(current, currentStats))
            return DbTextAiProblemDetection.Problem("mojibake-pattern", candidates);

        for (int i = 0; i < candidates.Count; i++)
        {
            DbTextAiCandidate candidate = candidates[i];
            if (candidate.IsNoOp || !candidate.IsRoundTrip || DbTextAiFeatureExtractor.HasUnsafeText(candidate.Text))
                continue;

            TextStats candidateStats = Analyze(candidate.Text);
            bool cjkImproved = candidateStats.CjkRatio >= 0.2f
                               && candidateStats.CjkRatio >= currentStats.CjkRatio + 0.2f;
            bool cadTermsImproved = CadKeywordRatio(candidate.Text) >= CadKeywordRatio(current) + 0.15f;
            bool sourceLooksRelevant = ContainsSource(candidate.Source, "big5")
                                       || ContainsSource(candidate.Source, "gbk")
                                       || ContainsSource(candidate.Source, "utf8");

            if (sourceLooksRelevant && (cjkImproved || cadTermsImproved))
                return DbTextAiProblemDetection.Problem("roundtrip-candidate-improved", candidates);
        }

        return DbTextAiProblemDetection.NoProblem(candidates);
    }

    private static bool LooksLikeMojibake(string text, TextStats stats)
    {
        if (stats.CjkRatio > 0.1f || stats.ExtendedLatinRatio < 0.25f)
            return false;

        if (stats.AsciiRatio > 0.75f)
            return false;

        return ContainsCommonMojibakeChar(text);
    }

    private static TextStats Analyze(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new TextStats();

        int cjk = 0;
        int ascii = 0;
        int extendedLatin = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x7F)
                ascii++;
            if (IsCjk(ch))
                cjk++;
            if (IsExtendedLatin(ch))
                extendedLatin++;
        }

        float length = Math.Max(1, text.Length);
        return new TextStats
        {
            CjkRatio = cjk / length,
            AsciiRatio = ascii / length,
            ExtendedLatinRatio = extendedLatin / length
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

    private static bool ContainsCommonMojibakeChar(string text)
    {
        const string chars = "ÃÂÄÅÊËÐÏÓÔÕÖ×ØÙÚÛÜÝÞßàáâãäåæçèéêëìíîïðñòóôõö÷øùúûüµ±¼½¾¿ÀÁÒ";
        foreach (char ch in text)
        {
            if (chars.IndexOf(ch) >= 0)
                return true;
        }

        return false;
    }

    private static bool ContainsSource(string source, string token)
    {
        return !string.IsNullOrEmpty(source)
               && source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsCjk(char ch)
    {
        return (ch >= '\u3400' && ch <= '\u4DBF')
               || (ch >= '\u4E00' && ch <= '\u9FFF')
               || (ch >= '\uF900' && ch <= '\uFAFF');
    }

    private static bool IsExtendedLatin(char ch)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return ch >= '\u0080'
               && ch <= '\u024F'
               && (category == UnicodeCategory.UppercaseLetter
                   || category == UnicodeCategory.LowercaseLetter
                   || category == UnicodeCategory.ModifierLetter
                   || category == UnicodeCategory.OtherLetter
                   || category == UnicodeCategory.OtherPunctuation
                   || category == UnicodeCategory.MathSymbol
                   || category == UnicodeCategory.CurrencySymbol
                   || category == UnicodeCategory.OtherSymbol);
    }

    private struct TextStats
    {
        public float CjkRatio;
        public float AsciiRatio;
        public float ExtendedLatinRatio;
    }
}
