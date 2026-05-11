using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using AFR.DbTextRepairModel;

namespace AFR.Services.DbTextRepair;

internal static class DbTextRepairCandidateGenerator
{
#if !NETFRAMEWORK
    private static int _providerRegistered;
#endif

    public static IReadOnlyList<DbTextRepairCandidate> BuildCandidates(string currentText, DbTextRepairModelIndex index)
    {
        var candidates = new List<DbTextRepairCandidate>();
        AddCandidate(candidates, currentText, "current-noop", "当前文本");

        if (TryGenerateBig5CarrierToGbkCandidate(currentText, out string big5Candidate, out string big5Reason))
            AddCandidate(candidates, big5Candidate, "big5-carrier-to-gbk", big5Reason);

        foreach (string historical in index.GetHistoricalCandidates(currentText))
            AddCandidate(candidates, historical, "historical-label", "历史人工标签");

        return candidates
            .OrderBy(c => c.Source.IndexOf("current-noop", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
            .ThenBy(c => c.Text, StringComparer.Ordinal)
            .ToList();
    }

    public static bool TryGenerateBig5CarrierToGbkCandidate(string currentText, out string candidate, out string reason)
    {
        candidate = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrEmpty(currentText))
        {
            reason = "empty";
            return false;
        }

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

            byte[] carrierBytes = big5.GetBytes(currentText);
            string decoded = gbk.GetString(carrierBytes);
            if (string.IsNullOrEmpty(decoded) || string.Equals(decoded, currentText, StringComparison.Ordinal))
            {
                reason = "same";
                return false;
            }

            candidate = decoded;
            reason = "big5-carrier-to-gbk";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
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

    private static void AddCandidate(List<DbTextRepairCandidate> candidates, string text, string source, string reason)
    {
        if (string.IsNullOrEmpty(text))
            return;

        DbTextRepairCandidate? existing = candidates.FirstOrDefault(c => string.Equals(c.Text, text, StringComparison.Ordinal));
        if (existing != null)
        {
            existing.AddSource(source, reason);
            return;
        }

        candidates.Add(new DbTextRepairCandidate(text, source, reason));
    }
}

internal sealed class DbTextRepairCandidate
{
    public DbTextRepairCandidate(string text, string source, string reason)
    {
        Text = text;
        Source = source;
        Reason = reason;
    }

    public string Text { get; }
    public string Source { get; private set; }
    public string Reason { get; private set; }
    public bool HasNeuralScore { get; private set; }
    public float NeuralScore { get; private set; }

    public void AddSource(string source, string reason)
    {
        if (!string.IsNullOrEmpty(source) && Source.IndexOf(source, StringComparison.OrdinalIgnoreCase) < 0)
            Source += "+" + source;
        if (!string.IsNullOrEmpty(reason) && Reason.IndexOf(reason, StringComparison.OrdinalIgnoreCase) < 0)
            Reason += "; " + reason;
    }

    public void SetNeuralScore(float score)
    {
        NeuralScore = score;
        HasNeuralScore = true;
    }
}
