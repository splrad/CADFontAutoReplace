using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;

namespace AFR_ACAD2026.Services;

/// <summary>
/// 单个样式的字体替换规格。
/// </summary>
internal sealed record StyleFontReplacement(
    string StyleName,
    bool IsTrueType,
    string MainFontReplacement,
    string BigFontReplacement);

/// <summary>
/// 使用配置的备用字体替换文字样式中的缺失字体。
/// 仅修改已确认缺失的样式 — 正常字体不受影响。
/// </summary>
internal static class FontReplacer
{
    /// <summary>
    /// 使用指定的备用字体替换缺失字体。
    /// TrueType 缺失 → 用 trueTypeFont 替换；SHX 缺失 → 用 mainFont 替换；大字体缺失 → 用 bigFont 替换。
    /// 返回被修改的样式数量。
    /// </summary>
    public static int ReplaceMissingFonts(
        Database db,
        IReadOnlyList<FontCheckResult> missingFonts,
        string mainFont,
        string bigFont,
        string trueTypeFont)
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
                if (missing.IsMainFontMissing)
                {
                    if (missing.IsTrueType)
                    {
                        // TrueType 只用 TrueType 字体替换
                        if (!string.IsNullOrEmpty(trueTypeFont))
                        {
                            // CharacterSet=0, PitchAndFamily=0 使用默认值，
                            // AutoCAD 加载时不会"修正"默认值，避免内部状态与 DWG 不一致。
                            // 注意: 不能保留原字体的值 — 不同字体的 PitchAndFamily 不同，
                            // 保留会导致 AutoCAD 每次加载都"修正"为实际值，触发 ST "已修改"弹窗。
                            style.Font = new FontDescriptor(trueTypeFont, false, false, 0, 0);
                            style.FileName = string.Empty;
                            // TrueType 样式不支持大字体，清空避免残留
                            style.BigFontFileName = string.Empty;
                            changed = true;
                        }
                    }
                    else
                    {
                        // SHX 只用 SHX 字体替换
                        if (!string.IsNullOrEmpty(mainFont))
                        {
                            // 必须先清除 TrueType 属性再设置 FileName，
                            // 否则设置 Font 可能重置 FileName
                            if (!string.IsNullOrEmpty(style.Font.TypeFace))
                                style.Font = new FontDescriptor("", false, false, 0, 0);
                            style.FileName = mainFont;
                            changed = true;
                        }
                    }
                }

                // 若大字体缺失且已配置替换字体，则执行替换
                // TrueType 样式不支持大字体，跳过
                if (missing.IsBigFontMissing && !missing.IsTrueType && !string.IsNullOrEmpty(bigFont))
                {
                    style.BigFontFileName = bigFont;
                    changed = true;
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

    /// <summary>
    /// 按样式名称与指定的替换字体进行逐一替换。
    /// 用于手动逐一指定替换字体的场景（仅影响当前图纸，不写入注册表）。
    /// </summary>
    public static int ReplaceByStyleMapping(
        Database db,
        IReadOnlyList<StyleFontReplacement> replacements)
    {
        if (replacements.Count == 0) return 0;

        var log = LogService.Instance;
        int replaceCount = 0;

        var map = new Dictionary<string, StyleFontReplacement>(replacements.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in replacements)
            map.TryAdd(r.StyleName, r);

        using var tr = db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!map.TryGetValue(style.Name, out var replacement)) continue;

                bool changed = false;
                style.UpgradeOpen();

                if (!string.IsNullOrEmpty(replacement.MainFontReplacement))
                {
                    if (replacement.IsTrueType)
                    {
                        style.Font = new FontDescriptor(replacement.MainFontReplacement, false, false, 0, 0);
                        style.FileName = string.Empty;
                        // TrueType 样式不支持大字体，清空避免残留
                        style.BigFontFileName = string.Empty;
                    }
                    else
                    {
                        // 必须先清除 TrueType 属性再设置 FileName
                        if (!string.IsNullOrEmpty(style.Font.TypeFace))
                            style.Font = new FontDescriptor("", false, false, 0, 0);
                        style.FileName = replacement.MainFontReplacement;
                    }
                    changed = true;
                }

                // TrueType 样式不支持大字体，跳过
                if (!replacement.IsTrueType && !string.IsNullOrEmpty(replacement.BigFontReplacement))
                {
                    style.BigFontFileName = replacement.BigFontReplacement;
                    changed = true;
                }

                if (changed) replaceCount++;
            }
            catch (Exception ex)
            {
                log.Error($"手动替换样式 {id} 的字体失败", ex);
            }
        }

        tr.Commit();
        return replaceCount;
    }

    }
