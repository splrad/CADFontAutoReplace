using System.Collections.Generic;
using AFR.GlyphCore.TextRepair;

namespace AFR.Services.GlyphCore.TextRepair;

internal sealed class GlyphCoreTextRepairAdvisor
{
    private readonly IGlyphCoreTextRepairScorer _scorer;

    public GlyphCoreTextRepairAdvisor()
    {
        _scorer = GlyphCoreTextRepairScorerFactory.CreateDefault();
    }

    public string AiStatus => _scorer.Status;
    public bool IsAiAvailable => _scorer.IsAvailable;

    public GlyphCoreTextRepairDecision Evaluate(
        GlyphCoreTextRepairContext context,
        IReadOnlyList<GlyphCoreTextRepairCandidate> candidates)
    {
        ScoreCandidates(context, candidates);
        return GlyphCoreTextRepairDecisionEngine.Evaluate(context, candidates, _scorer);
    }

    public void ScoreCandidates(GlyphCoreTextRepairContext context, IReadOnlyList<GlyphCoreTextRepairCandidate> candidates)
    {
        if (!_scorer.IsAvailable)
            return;

        for (int i = 0; i < candidates.Count; i++)
        {
            GlyphCoreTextRepairCandidate candidate = candidates[i];
            float[] features = GlyphCoreTextRepairFeatureExtractor.Extract(context, candidate);
            if (_scorer.TryScore(context, candidate, features, out float score, out string error))
                candidate.SetAiScore(score);
            else
                candidate.SetAiScoreError(error);
        }
    }
}

