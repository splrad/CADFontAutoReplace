using System;
using System.Collections.Generic;
using System.Linq;

namespace AFR.WenShu.DbText;

internal static class WenShuDbTextDecisionEngine
{
    public static WenShuDbTextDecision Evaluate(
        WenShuDbTextContext context,
        IReadOnlyList<WenShuDbTextCandidate> candidates,
        IWenShuDbTextScorer scorer)
    {
        string summary = BuildSummary(candidates, scorer);

        if (context.IsFromExternalReference)
            return WenShuDbTextDecision.Unsafe("xref-or-dependent-block", summary);

        if (WenShuDbTextFeatureExtractor.HasUnsafeText(context.CurrentText))
            return WenShuDbTextDecision.Unsafe("unsafe-current-text", summary);

        if (!scorer.IsAvailable)
            return WenShuDbTextDecision.Skip("ai-model-unavailable", summary);

        List<WenShuDbTextCandidate> scored = candidates
            .Where(c => c.HasAiScore)
            .OrderByDescending(c => c.AiScore)
            .ToList();
        if (scored.Count == 0)
            return WenShuDbTextDecision.Skip("no-ai-score", summary);

        WenShuDbTextCandidate best = scored[0];
        WenShuDbTextCandidate? second = scored.Count > 1 ? scored[1] : null;
        float margin = best.AiScore - (second?.AiScore ?? 0);

        if (best.IsNoOp || string.Equals(best.Text, context.CurrentText, StringComparison.Ordinal))
            return WenShuDbTextDecision.Skip("ai-selected-current", summary);

        if (!best.IsRoundTrip)
            return WenShuDbTextDecision.Unsafe("candidate-not-roundtrip", summary);

        if (WenShuDbTextFeatureExtractor.HasUnsafeText(best.Text))
            return WenShuDbTextDecision.Unsafe("unsafe-candidate-text", summary);

        if (best.AiScore < WenShuDbTextConstants.MinimumConfidence)
            return WenShuDbTextDecision.Skip("low-confidence", summary);

        if (margin < WenShuDbTextConstants.MinimumScoreMargin)
            return WenShuDbTextDecision.Skip("score-margin-too-small", summary);

        return WenShuDbTextDecision.Repair(best.Text, "ai-high-confidence", summary);
    }

    private static string BuildSummary(IReadOnlyList<WenShuDbTextCandidate> candidates, IWenShuDbTextScorer scorer)
    {
        WenShuDbTextCandidate? best = candidates
            .Where(c => c.HasAiScore)
            .OrderByDescending(c => c.AiScore)
            .FirstOrDefault();

        string status = $"ai={scorer.Status}";
        if (best == null)
            return status;

        return $"{status}, best='{Trim(best.Text)}', score={best.AiScore:0.000}, source={best.Source}";
    }

    private static string Trim(string text)
    {
        return text.Length <= 40 ? text : text.Substring(0, 40) + "...";
    }
}

