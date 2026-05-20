using Autodesk.AutoCAD.DatabaseServices;
using AFR.Services;

namespace AFR.FontMapping;

internal sealed class MTextInlineFontScanResult
{
    internal MTextInlineFontScanResult(
        Dictionary<string, InlineFontType> inlineFonts,
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

    internal Dictionary<string, InlineFontType> InlineFonts { get; }
    internal int MTextCount { get; }
    internal int FragmentExpansionAttempts { get; }
    internal int FragmentExpansionSuccesses { get; }
    internal int FragmentExpansionFailures { get; }
    internal int FragmentCount { get; }
}

/// <summary>
/// 扫描 AutoCAD 数据库中所有 MText（多行文字）实体，提取其内联字体引用并验证 fragment 可展开性。
/// <para>
/// 遍历所有 BlockTableRecord（包括 ModelSpace、PaperSpace 和嵌套块定义），
/// 对每个 MText 实体调用 <see cref="MTextFontParser.ParseInlineFonts"/> 解析其 Contents 属性；
/// 发现内联字体后再调用 <see cref="MText.ExplodeFragments(MTextFragmentCallback)"/>，
/// 但不把这次扫描枚举产生的临时 fragment 计入实际运行时映射结果。
/// 扫描阶段必须保持只读，不主动失效图形缓存或调用 Entity.Draw，避免污染块定义的图形状态。
/// 无法访问的实体或块表记录会被静默跳过。
/// </para>
/// </summary>
internal static class MTextInlineFontScanner
{
    /// <summary>
    /// 扫描数据库中所有 MText 实体的内联字体引用，并对包含内联字体的实体主动触发 fragment 展开。
    /// </summary>
    /// <param name="db">要扫描的 AutoCAD 数据库。</param>
    /// <returns>扫描到的内联字体、MText 数量与 fragment 展开统计。</returns>
    internal static MTextInlineFontScanResult ScanInlineFonts(Database db)
    {
        var result = new Dictionary<string, InlineFontType>(StringComparer.OrdinalIgnoreCase);
        int mTextCount = 0;
        int fragmentExpansionAttempts = 0;
        int fragmentExpansionSuccesses = 0;
        int fragmentExpansionFailures = 0;
        int fragmentCount = 0;

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

                                var entityFonts = new Dictionary<string, InlineFontType>(StringComparer.OrdinalIgnoreCase);
                                MTextFontParser.ParseInlineFonts(mtext.Contents, entityFonts);
                                foreach (var pair in entityFonts)
                                    result.TryAdd(pair.Key, pair.Value);

                                if (entityFonts.Count > 0)
                                {
                                    fragmentExpansionAttempts++;
                                    var state = new FragmentExpansionState();
                                    try
                                    {
                                        using (MTextInlineFontHook.SuppressInlineRuntimeMapping())
                                        {
                                            mtext.ExplodeFragments(CountFragment, state);
                                        }

                                        fragmentExpansionSuccesses++;
                                        fragmentCount += state.FragmentCount;
                                    }
                                    catch (Exception ex)
                                    {
                                        fragmentExpansionFailures++;
                                        DiagnosticLogger.Log(
                                            "MTextInlineFontScanner",
                                            $"MText fragment 展开失败 ObjectId={entId}: {ex.Message}");
                                    }
                                }
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

        MTextInlineFontHook.ReplaceInlineFontCandidates(result);

        return new MTextInlineFontScanResult(
            result,
            mTextCount,
            fragmentExpansionAttempts,
            fragmentExpansionSuccesses,
            fragmentExpansionFailures,
            fragmentCount);
    }

    private static MTextFragmentCallbackStatus CountFragment(MTextFragment fragment, object userData)
    {
        if (userData is FragmentExpansionState state)
            state.FragmentCount++;

        return MTextFragmentCallbackStatus.Continue;
    }

    private sealed class FragmentExpansionState
    {
        internal int FragmentCount { get; set; }
    }
}
