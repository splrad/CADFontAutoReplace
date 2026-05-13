namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairScorerFactory
{
    private static readonly object Sync = new();
    private static IGlyphCoreTextRepairScorer? _cached;

    public static IGlyphCoreTextRepairScorer CreateDefault()
    {
        lock (Sync)
        {
            if (_cached != null)
                return _cached;

            _cached = GlyphCoreTextRepairEmbeddedOnnxScorer.TryCreate(out IGlyphCoreTextRepairScorer scorer, out string error)
                ? scorer
                : new GlyphCoreTextRepairUnavailableScorer(error);
            return _cached;
        }
    }

}

