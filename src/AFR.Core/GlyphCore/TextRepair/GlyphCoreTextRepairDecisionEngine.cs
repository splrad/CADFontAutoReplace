using System;
using System.Collections.Generic;
using System.Linq;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairDecisionEngine
{
    public static GlyphCoreTextRepairDecision Evaluate(
        GlyphCoreTextRepairContext context,
        IReadOnlyList<GlyphCoreTextRepairCandidate> candidates,
        IGlyphCoreTextRepairScorer scorer)
    {
        string summary = BuildSummary(candidates, scorer);

        if (context.IsFromExternalReference)
            return GlyphCoreTextRepairDecision.Unsafe("xref-or-dependent-block", summary);

        if (!scorer.IsAvailable)
            return GlyphCoreTextRepairDecision.Skip("ai-model-unavailable", summary);

        List<GlyphCoreTextRepairCandidate> scored = candidates
            .Where(c => c.HasAiScore)
            .OrderByDescending(c => c.AiScore)
            .ToList();
        if (scored.Count == 0)
            return GlyphCoreTextRepairDecision.Skip("no-ai-score", summary);

        GlyphCoreTextRepairCandidate best = scored[0];

        if (best.IsNoOp || string.Equals(best.Text, context.CurrentText, StringComparison.Ordinal))
            return GlyphCoreTextRepairDecision.Skip("ai-selected-current", summary);

        if (string.IsNullOrEmpty(best.Text))
            return GlyphCoreTextRepairDecision.Unsafe("candidate-empty", summary);

        return GlyphCoreTextRepairDecision.Repair(best.Text, "ai-selected-by-evidence", summary);
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

        return $"{status}, best='{Trim(best.Text)}', score={best.AiScore:0.000}, source={best.Source}";
    }

    private static string Trim(string text)
    {
        return text.Length <= 40 ? text : text.Substring(0, 40) + "...";
    }
}

