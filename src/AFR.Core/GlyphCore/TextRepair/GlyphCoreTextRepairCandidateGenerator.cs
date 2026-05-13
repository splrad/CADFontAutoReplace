using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairCandidateGenerator
{
#if !NETFRAMEWORK
    private static int _providerRegistered;
#endif

    public static IReadOnlyList<GlyphCoreTextRepairCandidate> BuildCandidates(string currentText)
    {
        var candidates = new List<GlyphCoreTextRepairCandidate>();
        AddCandidate(candidates, currentText, "current-noop", "当前文本", isRoundTrip: true);

        TryAddConversion(candidates, currentText, 950, 936, "big5-carrier-to-gbk");
        TryAddConversion(candidates, currentText, 936, 950, "gbk-carrier-to-big5");
        TryAddConversion(candidates, currentText, Encoding.UTF8.CodePage, 936, "utf8-carrier-to-gbk");
        TryAddConversion(candidates, currentText, 936, Encoding.UTF8.CodePage, "gbk-carrier-to-utf8");

        return candidates
            .OrderBy(c => c.IsNoOp ? 1 : 0)
            .ThenBy(c => c.Text, StringComparer.Ordinal)
            .ToList();
    }

    private static void TryAddConversion(
        List<GlyphCoreTextRepairCandidate> candidates,
        string currentText,
        int carrierCodePage,
        int targetCodePage,
        string source)
    {
        if (string.IsNullOrEmpty(currentText))
            return;

        if (!TryConvertCarrier(currentText, carrierCodePage, targetCodePage, out string candidate, out bool roundTrip, out string reason))
            return;

        AddCandidate(candidates, candidate, source, reason, roundTrip);
    }

    private static bool TryConvertCarrier(
        string currentText,
        int carrierCodePage,
        int targetCodePage,
        out string candidate,
        out bool roundTrip,
        out string reason)
    {
        candidate = string.Empty;
        roundTrip = false;
        reason = string.Empty;

        try
        {
            EnsureCodePages();
            Encoding carrier = GetStrictEncoding(carrierCodePage);
            Encoding target = GetStrictEncoding(targetCodePage);

            byte[] carrierBytes = carrier.GetBytes(currentText);
            string decoded = target.GetString(carrierBytes);
            if (string.IsNullOrEmpty(decoded) || string.Equals(decoded, currentText, StringComparison.Ordinal))
            {
                reason = "same";
                return false;
            }

            byte[] candidateBytes = target.GetBytes(decoded);
            roundTrip = carrierBytes.SequenceEqual(candidateBytes)
                        && string.Equals(carrier.GetString(candidateBytes), currentText, StringComparison.Ordinal);
            candidate = decoded;
            reason = roundTrip ? "roundtrip-ok" : "roundtrip-failed";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }
    }

    private static Encoding GetStrictEncoding(int codePage)
    {
        if (codePage == Encoding.UTF8.CodePage)
            return new UTF8Encoding(false, throwOnInvalidBytes: true);

        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static void EnsureCodePages()
    {
#if !NETFRAMEWORK
        if (Interlocked.Exchange(ref _providerRegistered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
    }

    private static void AddCandidate(
        List<GlyphCoreTextRepairCandidate> candidates,
        string text,
        string source,
        string reason,
        bool isRoundTrip)
    {
        if (string.IsNullOrEmpty(text))
            return;

        GlyphCoreTextRepairCandidate? existing = candidates.FirstOrDefault(c => string.Equals(c.Text, text, StringComparison.Ordinal));
        if (existing != null)
        {
            existing.AddSource(source, reason, isRoundTrip);
            return;
        }

        candidates.Add(new GlyphCoreTextRepairCandidate(text, source, reason, isRoundTrip));
    }
}

