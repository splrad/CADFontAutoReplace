using System;
using System.Collections.Generic;
using System.Linq;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairDecisionEngine
{
    private const float MinimumConfidence = 0.75f;
    private const float MinimumScoreMargin = 0.02f;
    private const float NativeEvidenceMinimumConfidence = 0.55f;
    private const float NativeEvidenceMinimumMargin = 0.10f;

    public static GlyphCoreTextRepairDecision Evaluate(
        GlyphCoreTextRepairContext context,
        IReadOnlyList<GlyphCoreTextRepairCandidate> candidates,
        IGlyphCoreTextRepairScorer scorer)
    {
        string summary = BuildSummary(candidates, scorer);

        if (context.IsFromExternalReference)
            return GlyphCoreTextRepairDecision.Unsafe("xref-or-dependent-block", summary);

        if (GlyphCoreTextRepairFeatureExtractor.HasUnsafeText(context.CurrentText))
            return GlyphCoreTextRepairDecision.Unsafe("unsafe-current-text", summary);

        if (!scorer.IsAvailable)
            return GlyphCoreTextRepairDecision.Skip("ai-model-unavailable", summary);

        List<GlyphCoreTextRepairCandidate> scored = candidates
            .Where(c => c.HasAiScore)
            .OrderByDescending(c => c.AiScore)
            .ToList();
        if (scored.Count == 0)
            return GlyphCoreTextRepairDecision.Skip("no-ai-score", summary);

        GlyphCoreTextRepairCandidate best = scored[0];
        GlyphCoreTextRepairCandidate? second = FindSecondDistinctOutput(scored, best);
        float secondScore = second == null ? 0f : second.AiScore;
        float margin = best.AiScore - secondScore;

        if (best.IsNoOp || string.Equals(best.Text, context.CurrentText, StringComparison.Ordinal))
            return GlyphCoreTextRepairDecision.Skip("ai-selected-current", summary);

        if (string.IsNullOrEmpty(best.Text))
            return GlyphCoreTextRepairDecision.Unsafe("candidate-empty", summary);

        if (GlyphCoreTextRepairFeatureExtractor.HasUnsafeRepairCandidateText(best.Text))
            return GlyphCoreTextRepairDecision.Unsafe("unsafe-candidate-text", summary);

        if (!best.IsRoundTrip)
            return GlyphCoreTextRepairDecision.Skip("candidate-not-roundtrip", summary);

        if (best.AiScore < MinimumConfidence)
        {
            if (CanConservativelyAcceptNativeEvidence(context, best, margin))
                return GlyphCoreTextRepairDecision.Repair(best.Text, "ai-selected-by-native-evidence", summary);

            return GlyphCoreTextRepairDecision.Skip("low-confidence", summary);
        }

        if (margin < MinimumScoreMargin)
        {
            if (CanConservativelyAcceptNativeEvidence(context, best, margin))
                return GlyphCoreTextRepairDecision.Repair(best.Text, "ai-selected-by-native-evidence", summary);

            return GlyphCoreTextRepairDecision.Skip("score-margin-too-small", summary);
        }

        return GlyphCoreTextRepairDecision.Repair(best.Text, "ai-selected-by-model", summary);
    }

    private static bool CanConservativelyAcceptNativeEvidence(
        GlyphCoreTextRepairContext context,
        GlyphCoreTextRepairCandidate best,
        float margin)
    {
        if (!HasStrongNativeDecodeEvidence(context))
            return false;

        if (!IsEvidenceAlignedCandidate(context, best.Source))
            return false;

        if (best.AiScore < NativeEvidenceMinimumConfidence)
            return false;

        if (margin < NativeEvidenceMinimumMargin)
            return false;

        return true;
    }

    private static bool HasStrongNativeDecodeEvidence(GlyphCoreTextRepairContext context)
    {
        if (!context.HasNativeDecodeEvidence || !context.NativeDecodeFamilyMismatch)
            return false;

        if (!IsEvidenceScope(context, "ripple")
            && !IsEvidenceScope(context, "document-family")
            && !IsHookHitType(context.NativeDecodeHookHitType, "dbtext"))
            return false;

        if (context.NativeDecodeObjectCorrelation > 0)
            return true;

        return context.NativeDecodeClusterCorrelation > 0
               && (IsEvidenceScope(context, "cluster")
                   || IsEvidenceScope(context, "ripple")
                   || IsEvidenceScope(context, "document-family"));
    }

    private static bool IsEvidenceAlignedCandidate(GlyphCoreTextRepairContext context, string candidateSource)
    {
        if (string.IsNullOrEmpty(candidateSource) || !context.NativeDecodeFamilyMismatch)
            return false;

        if (IsCodePageFamily(context.NativeDecodeSourceCodePageFamily, "big5")
            && IsCodePageFamily(context.NativeDecodeAppliedCodePageFamily, "gbk"))
            return ContainsToken(candidateSource, "big5-carrier-to-gbk");

        if (IsCodePageFamily(context.NativeDecodeSourceCodePageFamily, "gbk")
            && IsCodePageFamily(context.NativeDecodeAppliedCodePageFamily, "big5"))
            return ContainsToken(candidateSource, "gbk-carrier-to-big5");

        if (IsCodePageFamily(context.NativeDecodeSourceCodePageFamily, "utf8")
            && IsCodePageFamily(context.NativeDecodeAppliedCodePageFamily, "gbk"))
            return ContainsToken(candidateSource, "utf8-carrier-to-gbk");

        if (IsCodePageFamily(context.NativeDecodeSourceCodePageFamily, "gbk")
            && IsCodePageFamily(context.NativeDecodeAppliedCodePageFamily, "utf8"))
            return ContainsToken(candidateSource, "gbk-carrier-to-utf8");

        return false;
    }

    private static string BuildSummary(IReadOnlyList<GlyphCoreTextRepairCandidate> candidates, IGlyphCoreTextRepairScorer scorer)
    {
        GlyphCoreTextRepairCandidate? best = candidates
            .Where(c => c.HasAiScore)
            .OrderByDescending(c => c.AiScore)
            .FirstOrDefault();

        string status = $"ai={scorer.Status}";
        if (best == null)
        {
            GlyphCoreTextRepairCandidate? failed = candidates.FirstOrDefault(c => c.HasAiScoreError);
            if (failed == null)
                return status;

            return $"{status}, scoreError='{Trim(failed.AiScoreError)}', source={failed.Source}";
        }

        GlyphCoreTextRepairCandidate? second = FindSecondDistinctOutput(
            candidates
                .Where(c => c.HasAiScore)
                .OrderByDescending(c => c.AiScore)
                .ToList(),
            best);
        string margin = second == null
            ? "margin=1.000"
            : $"margin={best.AiScore - second.AiScore:0.000}";
        return $"{status}, best='{Trim(best.Text)}', score={best.AiScore:0.000}, {margin}, source={best.Source}";
    }

    private static GlyphCoreTextRepairCandidate? FindSecondDistinctOutput(
        IReadOnlyList<GlyphCoreTextRepairCandidate> scored,
        GlyphCoreTextRepairCandidate best)
    {
        for (int i = 0; i < scored.Count; i++)
        {
            GlyphCoreTextRepairCandidate candidate = scored[i];
            if (ReferenceEquals(candidate, best))
                continue;
            if (!string.Equals(candidate.Text, best.Text, StringComparison.Ordinal))
                return candidate;
        }

        return null;
    }

    private static string Trim(string text)
    {
        return text.Length <= 40 ? text : text.Substring(0, 40) + "...";
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
}

