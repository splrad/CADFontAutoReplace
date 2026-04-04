using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using AFR.Models;

namespace AFR.Services;

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

        // 清除缓存，确保在当前图纸上下文中重新验证字体可用性
        FontDetector.ClearCaches();

        // 预校验替换字体是否可用，避免将样式写成不可用字体
        bool mainFontValid = !string.IsNullOrEmpty(mainFont)
            && FontDetector.IsShxFontAvailable(mainFont, db);
        bool bigFontValid = !string.IsNullOrEmpty(bigFont)
            && FontDetector.IsShxFontAvailable(bigFont, db);
        bool trueTypeFontValid = !string.IsNullOrEmpty(trueTypeFont)
            && FontDetector.IsTrueTypeFontAvailable(trueTypeFont, db);

        if (!string.IsNullOrEmpty(mainFont) && !mainFontValid)
            log.Warning($"配置的 SHX 替换字体 '{mainFont}' 在当前环境中不可用，将跳过 SHX 主字体替换");
        if (!string.IsNullOrEmpty(bigFont) && !bigFontValid)
            log.Warning($"配置的大字体替换字体 '{bigFont}' 在当前环境中不可用，将跳过大字体替换");
        if (!string.IsNullOrEmpty(trueTypeFont) && !trueTypeFontValid)
            log.Warning($"配置的 TrueType 替换字体 '{trueTypeFont}' 在当前环境中不可用，将跳过 TrueType 替换");

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
                        // TrueType 只用 TrueType 字体替换（需通过可用性校验）
                        if (trueTypeFontValid)
                        {
                            var (charset, pitch) = FontDetector.GetTrueTypeFontMetrics(trueTypeFont);

                            // 诊断: 替换前状态
                            var before = style.Font;
                            log.Info($"[TT替换] 样式='{style.Name}' 替换前: TypeFace='{before.TypeFace}' FileName='{style.FileName}' CharSet={before.CharacterSet} Pitch={before.PitchAndFamily}");
                            log.Info($"[TT替换] 替换为: '{trueTypeFont}' CharSet={charset} Pitch={pitch}");

                            // 先清除 SHX 引用，再设置 TrueType
                            // 顺序关键: FileName 和 Font 有内部联动，
                            // 必须先清 FileName 再设 Font，与 SHX 分支的"先清后设"对称。
                            style.FileName = string.Empty;
                            style.BigFontFileName = string.Empty;
                            style.Font = new FontDescriptor(trueTypeFont, false, false, charset, pitch);

                            // 诊断: 替换后读回验证
                            var after = style.Font;
                            log.Info($"[TT替换] 替换后: TypeFace='{after.TypeFace}' FileName='{style.FileName}' CharSet={after.CharacterSet} Pitch={after.PitchAndFamily}");

                            changed = true;
                        }
                    }
                    else
                    {
                        // SHX 只用 SHX 字体替换（需通过可用性校验）
                        if (mainFontValid)
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
                if (missing.IsBigFontMissing && !missing.IsTrueType && bigFontValid)
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

        // 清除缓存，确保在当前图纸上下文中重新验证
        FontDetector.ClearCaches();

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
                        if (!FontDetector.IsTrueTypeFontAvailable(replacement.MainFontReplacement, db))
                        {
                            log.Warning($"手动替换: 样式 '{replacement.StyleName}' 的 TrueType 替换字体 '{replacement.MainFontReplacement}' 不可用，跳过");
                        }
                        else
                        {
                            var (charset, pitch) = FontDetector.GetTrueTypeFontMetrics(replacement.MainFontReplacement);
                            style.FileName = string.Empty;
                            style.BigFontFileName = string.Empty;
                            style.Font = new FontDescriptor(replacement.MainFontReplacement, false, false, charset, pitch);
                            changed = true;
                        }
                    }
                    else
                    {
                        if (!FontDetector.IsShxFontAvailable(replacement.MainFontReplacement, db))
                        {
                            log.Warning($"手动替换: 样式 '{replacement.StyleName}' 的 SHX 替换字体 '{replacement.MainFontReplacement}' 不可用，跳过");
                        }
                        else
                        {
                            // 必须先清除 TrueType 属性再设置 FileName
                            if (!string.IsNullOrEmpty(style.Font.TypeFace))
                                style.Font = new FontDescriptor("", false, false, 0, 0);
                            style.FileName = replacement.MainFontReplacement;
                            changed = true;
                        }
                    }
                }

                // TrueType 样式不支持大字体，跳过
                if (!replacement.IsTrueType && !string.IsNullOrEmpty(replacement.BigFontReplacement))
                {
                    if (!FontDetector.IsShxFontAvailable(replacement.BigFontReplacement, db))
                    {
                        log.Warning($"手动替换: 样式 '{replacement.StyleName}' 的大字体替换字体 '{replacement.BigFontReplacement}' 不可用，跳过");
                    }
                    else
                    {
                        style.BigFontFileName = replacement.BigFontReplacement;
                        changed = true;
                    }
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

    /// <summary>
    /// 清理样式表中"TrueType 可用但 SHX 缺失"的残留引用。
    /// 当样式同时有 TypeFace（已安装 TrueType）和 FileName（缺失 SHX）时，
    /// AutoCAD 使用 TrueType 渲染，SHX 引用实际无用。
    /// 但 Hook 会在加载阶段重定向缺失 SHX → 内部缓存与 DWG 不一致 → ST "已修改"弹窗。
    /// 清除 FileName 可消除不一致，同时不影响渲染（TrueType 仍可用）。
    /// </summary>
    public static int CleanupStaleShxReferences(Database db)
    {
        var log = LogService.Instance;
        int cleaned = 0;

        // 系统字体索引未就绪时跳过清理，避免误判
        if (!FontDetector.IsSystemFontIndexReady)
        {
            log.Warning("[清理] 系统字体索引尚未就绪，跳过残留 SHX 清理以避免误操作");
            return 0;
        }

        // 清除缓存，确保在当前图纸上下文中重新验证
        FontDetector.ClearCaches();

        using var tr = db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                var font = style.Font;

                // 仅处理有 TrueType 字族名的样式
                if (string.IsNullOrEmpty(font.TypeFace)) continue;

                // TrueType 必须已安装（通过系统字体索引或 FindFile 双重验证）
                if (!FontDetector.IsSystemFont(font.TypeFace)
                    && !FontDetector.IsTrueTypeFontAvailable(font.TypeFace, db))
                    continue;

                var fileName = style.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                // FileName 是 TrueType 文件 → 不需要清理
                if (fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 检查 SHX 是否存在
                bool shxExists;
                try
                {
                    string result = HostApplicationServices.Current.FindFile(fileName, db, FindFileHint.CompiledShapeFile);
                    shxExists = !string.IsNullOrEmpty(result);
                }
                catch { shxExists = false; }

                if (!shxExists)
                {
                    // 不带扩展名时再尝试 +.shx
                    if (!Path.HasExtension(fileName))
                    {
                        try
                        {
                            string result = HostApplicationServices.Current.FindFile(fileName + ".shx", db, FindFileHint.CompiledShapeFile);
                            shxExists = !string.IsNullOrEmpty(result);
                        }
                        catch { shxExists = false; }
                    }
                }

                if (shxExists) continue; // SHX 存在，无需清理

                // TrueType 可用 + SHX 缺失 → 清除残留 SHX 引用
                style.UpgradeOpen();
                log.Info($"[清理] 样式='{style.Name}' TrueType='{font.TypeFace}' 清除残留 FileName='{fileName}' BigFont='{style.BigFontFileName}'");
                style.FileName = string.Empty;
                style.BigFontFileName = string.Empty;
                cleaned++;
            }
            catch { }
        }

        tr.Commit();
        return cleaned;
    }

    }
