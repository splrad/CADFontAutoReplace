using System;
using System.Collections.Generic;
using System.Linq;

namespace AFR.DbTextAI;

internal static class DbTextAiDecisionEngine
{
    public static DbTextAiDecision Evaluate(
        DbTextAiContext context,
        IReadOnlyList<DbTextAiCandidate> candidates,
        IDbTextAiScorer scorer)
    {
        string summary = BuildSummary(candidates, scorer);

        if (context.IsFromExternalReference)
            return DbTextAiDecision.Unsafe("xref-or-dependent-block", summary);

        if (DbTextAiFeatureExtractor.HasUnsafeText(context.CurrentText))
            return DbTextAiDecision.Unsafe("unsafe-current-text", summary);

        if (!scorer.IsAvailable)
            return DbTextAiDecision.Skip("ai-model-unavailable", summary);

        List<DbTextAiCandidate> scored = candidates
            .Where(c => c.HasAiScore)
            .OrderByDescending(c => c.AiScore)
            .ToList();
        if (scored.Count == 0)
            return DbTextAiDecision.Skip("no-ai-score", summary);

        DbTextAiCandidate best = scored[0];
        DbTextAiCandidate? second = scored.Count > 1 ? scored[1] : null;
        float margin = best.AiScore - (second?.AiScore ?? 0);

        if (best.IsNoOp || string.Equals(best.Text, context.CurrentText, StringComparison.Ordinal))
            return DbTextAiDecision.Skip("ai-selected-current", summary);

        if (!best.IsRoundTrip)
            return DbTextAiDecision.Unsafe("candidate-not-roundtrip", summary);

        if (DbTextAiFeatureExtractor.HasUnsafeText(best.Text))
            return DbTextAiDecision.Unsafe("unsafe-candidate-text", summary);

        if (best.AiScore < DbTextAiConstants.MinimumConfidence)
            return DbTextAiDecision.Skip("low-confidence", summary);

        if (margin < DbTextAiConstants.MinimumScoreMargin)
            return DbTextAiDecision.Skip("score-margin-too-small", summary);

        return DbTextAiDecision.Repair(best.Text, "ai-high-confidence", summary);
    }

    private static string BuildSummary(IReadOnlyList<DbTextAiCandidate> candidates, IDbTextAiScorer scorer)
    {
        DbTextAiCandidate? best = candidates
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
