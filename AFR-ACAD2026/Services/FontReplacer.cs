using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;

namespace AFR_ACAD2026.Services;

/// <summary>
/// 使用配置的备用字体替换文字样式中的缺失字体。
/// 仅修改已确认缺失的样式 — 正常字体不受影响。
/// </summary>
internal static class FontReplacer
{
    /// <summary>
    /// 使用指定的备用字体替换缺失字体。
    /// 返回被修改的样式数量。
    /// </summary>
    public static int ReplaceMissingFonts(
        Database db,
        IReadOnlyList<FontCheckResult> missingFonts,
        string mainFont,
        string bigFont)
    {
        if (missingFonts.Count == 0) return 0;

        var log = LogService.Instance;
        int replaceCount = 0;

        // 预构建字典—O(1)查找替代线性搜索
        var missingMap = new Dictionary<string, FontCheckResult>(missingFonts.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < missingFonts.Count; i++)
        {
            missingMap.TryAdd(missingFonts[i].StyleName, missingFonts[i]);
        }

        using var tr = db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!missingMap.TryGetValue(style.Name, out var missing)) continue;

                bool changed = false;
                style.UpgradeOpen();

                // 若主字体缺失且已配置替换字体，则执行替换
                if (missing.IsMainFontMissing && !string.IsNullOrEmpty(mainFont))
                {
                    if (missing.IsTrueType)
                    {
                        string fontName = style.Font.TypeFace;
                        if (string.IsNullOrEmpty(fontName)) fontName = style.FileName;
                        // 清除 TrueType 描述符并切换为 SHX
                        style.Font = new FontDescriptor("", false, false, 0, 0);
                        style.FileName = mainFont;
                        changed = true;
                        log.FontTrueType($"[样式: {style.Name}]-TrueType字体缺失: {fontName} → 替换为: {mainFont}");
                    }
                    else
                    {
                        string oldFileName = style.FileName;
                        style.FileName = mainFont;
                        changed = true;
                        log.FontShx($"[样式: {style.Name}]-SHX字体缺失: {oldFileName} → 替换为: {mainFont}");
                    }
                }

                // 若大字体缺失且已配置替换字体，则执行替换
                if (missing.IsBigFontMissing && !string.IsNullOrEmpty(bigFont))
                {
                    string oldBigFont = style.BigFontFileName;
                    style.BigFontFileName = bigFont;
                    changed = true;
                    log.FontBigFont($"[样式: {style.Name}]-大字体缺失: {oldBigFont} → 替换为: {bigFont}");
                }

                if (changed) replaceCount++;
            }
            catch (Exception ex)
            {
                log.Error($"替换样式 {id} 的字体失败", ex);
            }
        }

        tr.Commit();
        return replaceCount;
    }

    }
