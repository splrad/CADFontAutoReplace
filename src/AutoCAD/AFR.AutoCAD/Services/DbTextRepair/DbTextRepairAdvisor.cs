using System.Collections.Generic;
using AFR.DbTextAI;

namespace AFR.Services.DbTextRepair;

internal sealed class DbTextRepairAdvisor
{
    private readonly IDbTextAiScorer _scorer;

    public DbTextRepairAdvisor()
    {
        _scorer = DbTextAiScorerFactory.CreateDefault();
    }

    public string AiStatus => _scorer.Status;
    public bool IsAiAvailable => _scorer.IsAvailable;

    public DbTextAiDecision Evaluate(
        DbTextAiContext context,
        IReadOnlyList<DbTextAiCandidate> candidates)
    {
        ScoreCandidates(context, candidates);
        return DbTextAiDecisionEngine.Evaluate(context, candidates, _scorer);
    }

    public void ScoreCandidates(DbTextAiContext context, IReadOnlyList<DbTextAiCandidate> candidates)
    {
        if (!_scorer.IsAvailable)
            return;

        for (int i = 0; i < candidates.Count; i++)
        {
            DbTextAiCandidate candidate = candidates[i];
            float[] features = DbTextAiFeatureExtractor.Extract(context, candidate);
            if (_scorer.TryScore(context, candidate, features, out float score, out _))
                candidate.SetAiScore(score);
        }
    }
}
