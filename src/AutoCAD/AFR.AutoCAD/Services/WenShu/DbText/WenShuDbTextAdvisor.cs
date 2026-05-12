using System.Collections.Generic;
using AFR.WenShu.DbText;

namespace AFR.Services.WenShu.DbText;

internal sealed class WenShuDbTextAdvisor
{
    private readonly IWenShuDbTextScorer _scorer;

    public WenShuDbTextAdvisor()
    {
        _scorer = WenShuDbTextScorerFactory.CreateDefault();
    }

    public string AiStatus => _scorer.Status;
    public bool IsAiAvailable => _scorer.IsAvailable;

    public WenShuDbTextDecision Evaluate(
        WenShuDbTextContext context,
        IReadOnlyList<WenShuDbTextCandidate> candidates)
    {
        ScoreCandidates(context, candidates);
        return WenShuDbTextDecisionEngine.Evaluate(context, candidates, _scorer);
    }

    public void ScoreCandidates(WenShuDbTextContext context, IReadOnlyList<WenShuDbTextCandidate> candidates)
    {
        if (!_scorer.IsAvailable)
            return;

        for (int i = 0; i < candidates.Count; i++)
        {
            WenShuDbTextCandidate candidate = candidates[i];
            float[] features = WenShuDbTextFeatureExtractor.Extract(context, candidate);
            if (_scorer.TryScore(context, candidate, features, out float score, out _))
                candidate.SetAiScore(score);
        }
    }
}

