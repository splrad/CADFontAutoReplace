namespace AFR.DbTextAI;

internal static class DbTextAiScorerFactory
{
    private static readonly object Sync = new();
    private static IDbTextAiScorer? _cached;

    public static IDbTextAiScorer CreateDefault()
    {
        lock (Sync)
        {
            if (_cached != null)
                return _cached;

            _cached = DbTextEmbeddedOnnxScorer.TryCreate(out IDbTextAiScorer scorer, out string error)
                ? scorer
                : new DbTextUnavailableAiScorer(error);
            return _cached;
        }
    }

}
