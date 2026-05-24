using Autodesk.AutoCAD.DatabaseServices;
namespace AFR.FontMapping;

internal sealed class MTextInlineFontScanResult
{
    internal MTextInlineFontScanResult(
        Dictionary<string, InlineFontCandidate> inlineFonts,
        int mTextCount,
        int fragmentExpansionAttempts,
        int fragmentExpansionSuccesses,
        int fragmentExpansionFailures,
        int fragmentCount)
    {
        InlineFonts = inlineFonts;
        MTextCount = mTextCount;
        FragmentExpansionAttempts = fragmentExpansionAttempts;
        FragmentExpansionSuccesses = fragmentExpansionSuccesses;
        FragmentExpansionFailures = fragmentExpansionFailures;
        FragmentCount = fragmentCount;
    }

    internal Dictionary<string, InlineFontCandidate> InlineFonts { get; }
    internal int MTextCount { get; }
    internal int FragmentExpansionAttempts { get; }
    internal int FragmentExpansionSuccesses { get; }
    internal int FragmentExpansionFailures { get; }
    internal int FragmentCount { get; }
}

/// <summary>
/// 扫描 AutoCAD 数据库中所有 MText（多行文字）实体，提取其内联字体引用。
/// <para>
/// 遍历所有 BlockTableRecord（包括 ModelSpace、PaperSpace 和嵌套块定义），
/// 对每个 MText 实体调用 <see cref="MTextFontParser.ParseInlineFonts"/> 解析其 Contents 属性。
/// 扫描阶段必须保持只读，不调用 <see cref="MText.ExplodeFragments(MTextFragmentCallback)"/>、
/// 不主动失效图形缓存或调用 Entity.Draw，避免污染块定义的图形状态。
/// 无法访问的实体或块表记录会被静默跳过。
/// </para>
/// </summary>
internal static class MTextInlineFontScanner
{
    /// <summary>
    /// 扫描数据库中所有 MText 实体的内联字体引用。
    /// </summary>
    /// <param name="db">要扫描的 AutoCAD 数据库。</param>
    /// <returns>扫描到的内联字体与 MText 数量；fragment 展开统计在扫描阶段固定为 0。</returns>
    internal static MTextInlineFontScanResult ScanInlineFonts(Database db)
    {
        var result = new Dictionary<string, InlineFontCandidate>(StringComparer.OrdinalIgnoreCase);
        int mTextCount = 0;

        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            foreach (ObjectId btrId in bt)
            {
                try
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);

                    foreach (ObjectId entId in btr)
                    {
                        try
                        {
                            if (tr.GetObject(entId, OpenMode.ForRead) is MText mtext)
                            {
                                mTextCount++;
                                string contents = mtext.Contents ?? string.Empty;
                                if (!ContainsInlineFontMarker(contents))
                                    continue;

                                var entityFonts = new Dictionary<string, InlineFontCandidate>(StringComparer.OrdinalIgnoreCase);
                                MTextFontParser.ParseInlineFonts(contents, entityFonts);
                                foreach (var pair in entityFonts)
                                    result.TryAdd(pair.Key, pair.Value);
                            }
                        }
                        catch
                        {
                            // 跳过无法访问的实体
                        }
                    }
                }
                catch
                {
                    // 跳过无法访问的块表记录
                }
            }

            tr.Commit();
        }

        return new MTextInlineFontScanResult(
            result,
            mTextCount,
            fragmentExpansionAttempts: 0,
            fragmentExpansionSuccesses: 0,
            fragmentExpansionFailures: 0,
            fragmentCount: 0);
    }

    private static bool ContainsInlineFontMarker(string contents)
        => contents.IndexOf("\\F", StringComparison.OrdinalIgnoreCase) >= 0;
}
