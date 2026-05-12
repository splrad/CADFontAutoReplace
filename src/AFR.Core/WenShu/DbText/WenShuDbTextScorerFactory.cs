namespace AFR.WenShu.DbText;

internal static class WenShuDbTextScorerFactory
{
    private static readonly object Sync = new();
    private static IWenShuDbTextScorer? _cached;

    public static IWenShuDbTextScorer CreateDefault()
    {
        lock (Sync)
        {
            if (_cached != null)
                return _cached;

            _cached = WenShuDbTextEmbeddedOnnxScorer.TryCreate(out IWenShuDbTextScorer scorer, out string error)
                ? scorer
                : new WenShuDbTextUnavailableScorer(error);
            return _cached;
        }
    }

}

