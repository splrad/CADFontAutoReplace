using System;
using System.Collections.Generic;
using System.Linq;
using AFR.DbTextRepairModel;

namespace AFR.Services.DbTextRepair;

internal sealed class DbTextRepairAdvisor
{
    private readonly DbTextRepairModelIndex _index;
    private readonly DbTextNeuralRanker? _ranker;
    private readonly string _rankerStatus;

    public DbTextRepairAdvisor(DbTextRepairModelIndex index)
    {
        _index = index;
        if (index.TryGetActiveNeuralParameters(out DbTextRepairModelRecord neuralRecord)
            && DbTextNeuralRanker.TryCreate(neuralRecord, out DbTextNeuralRanker? ranker, out string error))
        {
            _ranker = ranker;
            _rankerStatus = "loaded";
        }
        else
        {
            _rankerStatus = index.NeuralParameterRecordCount == 0 ? "none" : "stale-or-invalid";
        }
    }

    public bool HasActiveNeuralRanker => _ranker != null;
    public string NeuralRankerStatus => _rankerStatus;

    public DbTextRepairDecision Evaluate(
        DbTextRepairModelRecord context,
        IReadOnlyList<DbTextRepairCandidate> candidates)
    {
        ScoreCandidates(context, candidates);

        foreach (string candidateText in CandidateTextsForExactMatch(candidates))
        {
            if (_index.TryFindExact(
                    context.DrawingSha256,
                    context.Handle,
                    context.CurrentText,
                    candidateText,
                    out DbTextRepairModelRecord record,
                    out bool hasConflict))
            {
                if (DbTextRepairPolicy.CanAutoRepairByLabel(record, context.CurrentText, out string selectedText, out string reason))
                    return DbTextRepairDecision.Repair(selectedText, reason, BestNeuralSummary(candidates));

                return DbTextRepairDecision.Block("label-" + reason, BestNeuralSummary(candidates));
            }

            if (hasConflict)
                return DbTextRepairDecision.Block("conflict", BestNeuralSummary(candidates));
        }

        return DbTextRepairDecision.Abstain("no-exact-label", BestNeuralSummary(candidates));
    }

    public void ScoreCandidates(DbTextRepairModelRecord context, IReadOnlyList<DbTextRepairCandidate> candidates)
    {
        if (_ranker == null)
            return;

        foreach (DbTextRepairCandidate candidate in candidates)
            candidate.SetNeuralScore(_ranker.Score(context, candidate.Text, candidate.Source));
    }

    private static IEnumerable<string> CandidateTextsForExactMatch(IReadOnlyList<DbTextRepairCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DbTextRepairCandidate candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate.Text))
                continue;
            if (seen.Add(candidate.Text))
                yield return candidate.Text;
        }

        if (seen.Add(string.Empty))
            yield return string.Empty;
    }

    private static string BestNeuralSummary(IReadOnlyList<DbTextRepairCandidate> candidates)
    {
        DbTextRepairCandidate? best = candidates
            .Where(c => c.HasNeuralScore)
            .OrderByDescending(c => c.NeuralScore)
            .FirstOrDefault();

        if (best == null)
            return string.Empty;

        return $"ai-best='{Trim(best.Text)}', score={best.NeuralScore:0.000}, source={best.Source}";
    }

    private static string Trim(string text)
    {
        return text.Length <= 40 ? text : text.Substring(0, 40) + "...";
    }
}

internal sealed class DbTextRepairDecision
{
    private DbTextRepairDecision(string action, string selectedText, string reason, string neuralSummary)
    {
        Action = action;
        SelectedText = selectedText;
        Reason = reason;
        NeuralSummary = neuralSummary;
    }

    public string Action { get; }
    public string SelectedText { get; }
    public string Reason { get; }
    public string NeuralSummary { get; }

    public bool ShouldRepair => string.Equals(Action, "repair", StringComparison.Ordinal);
    public bool IsBlocked => string.Equals(Action, "block", StringComparison.Ordinal);

    public static DbTextRepairDecision Repair(string selectedText, string reason, string neuralSummary) => new("repair", selectedText, reason, neuralSummary);
    public static DbTextRepairDecision Block(string reason, string neuralSummary) => new("block", string.Empty, reason, neuralSummary);
    public static DbTextRepairDecision Abstain(string reason, string neuralSummary) => new("abstain", string.Empty, reason, neuralSummary);
}
