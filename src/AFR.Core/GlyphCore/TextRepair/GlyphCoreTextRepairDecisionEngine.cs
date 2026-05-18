using System;
using System.Collections.Generic;
using System.Linq;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairDecisionEngine
{
    private const float MinimumConfidence = 0.60f;
    private const float MinimumScoreMargin = 0.02f;

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
        float secondScore = scored.Count > 1 ? scored[1].AiScore : 0f;
        float margin = best.AiScore - secondScore;

        if (best.IsNoOp || string.Equals(best.Text, context.CurrentText, StringComparison.Ordinal))
            return GlyphCoreTextRepairDecision.Skip("ai-selected-current", summary);

        if (string.IsNullOrEmpty(best.Text))
            return GlyphCoreTextRepairDecision.Unsafe("candidate-empty", summary);

        if (GlyphCoreTextRepairFeatureExtractor.HasUnsafeText(best.Text))
            return GlyphCoreTextRepairDecision.Unsafe("unsafe-candidate-text", summary);

        if (!best.IsRoundTrip)
            return GlyphCoreTextRepairDecision.Skip("candidate-not-roundtrip", summary);

        if (best.AiScore < MinimumConfidence)
            return GlyphCoreTextRepairDecision.Skip("low-confidence", summary);

        if (margin < MinimumScoreMargin)
            return GlyphCoreTextRepairDecision.Skip("score-margin-too-small", summary);

        return GlyphCoreTextRepairDecision.Repair(best.Text, "ai-selected-by-model", summary);
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

        GlyphCoreTextRepairCandidate? second = candidates
            .Where(c => c.HasAiScore && !ReferenceEquals(c, best))
            .OrderByDescending(c => c.AiScore)
            .FirstOrDefault();
        string margin = second == null
            ? "margin=1.000"
            : $"margin={best.AiScore - second.AiScore:0.000}";
        return $"{status}, best='{Trim(best.Text)}', score={best.AiScore:0.000}, {margin}, source={best.Source}";
    }

    private static string Trim(string text)
    {
        return text.Length <= 40 ? text : text.Substring(0, 40) + "...";
    }
}

