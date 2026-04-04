using System.IO;
using System.Windows.Media;
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
        IReadOnlyList<FontCheckResult> missingFonts,
        string mainFont,
        string bigFont,
        string trueTypeFont,
        FontDetectionContext context)
    {
        if (missingFonts.Count == 0) return 0;

        var log = LogService.Instance;
        int replaceCount = 0;

        // 预校验替换字体是否可用，避免将样式写成不可用字体
        bool mainFontValid = !string.IsNullOrEmpty(mainFont)
            && FontDetector.IsShxFontAvailable(mainFont, context);
        bool bigFontValid = !string.IsNullOrEmpty(bigFont)
            && FontDetector.IsShxFontAvailable(bigFont, context);
        bool trueTypeFontValid = !string.IsNullOrEmpty(trueTypeFont)
            && FontDetector.IsTrueTypeFontAvailable(trueTypeFont, context);

        if (!string.IsNullOrEmpty(mainFont) && !mainFontValid)
            log.Warning($"配置的 SHX 替换字体 '{mainFont}' 在当前环境中不可用，将跳过 SHX 主字体替换");
        if (!string.IsNullOrEmpty(bigFont) && !bigFontValid)
            log.Warning($"配置的大字体替换字体 '{bigFont}' 在当前环境中不可用，将跳过大字体替换");
        if (!string.IsNullOrEmpty(trueTypeFont) && !trueTypeFontValid)
            log.Warning($"配置的 TrueType 替换字体 '{trueTypeFont}' 在当前环境中不可用，将跳过 TrueType 替换");

        // FontDescriptor 和 GDI 查询要求字族名（如 "SimSun"），而非文件名（如 "simsun.ttc"）
        if (trueTypeFontValid)
            trueTypeFont = NormalizeTrueTypeName(trueTypeFont, context);

        // 预构建字典—O(1)查找替代线性搜索
        var missingMap = new Dictionary<string, FontCheckResult>(missingFonts.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < missingFonts.Count; i++)
        {
            missingMap.TryAdd(missingFonts[i].StyleName, missingFonts[i]);
        }

        using var tr = context.Db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(context.Db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!missingMap.TryGetValue(style.Name, out var missing)) continue;

                // ShapeFile 样式用于复杂线型（ltypeshp.shx 等），替换会破坏线型结构
                if (style.IsShapeFile) continue;

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
                            var (charset, pitch) = FontDetector.GetTrueTypeFontMetrics(trueTypeFont, context);

                            // 诊断: 替换前状态
                            var before = style.Font;
                            log.Info($"[TT替换] 样式='{style.Name}' 替换前: TypeFace='{before.TypeFace}' FileName='{style.FileName}' CharSet={before.CharacterSet} Pitch={before.PitchAndFamily}");
                            log.Info($"[TT替换] 替换为: '{trueTypeFont}' CharSet={charset} Pitch={pitch}");

                            // 先清除 SHX 引用，再设置 TrueType
                            // 顺序关键: AutoCAD 要求 FileName 为有效 SHX 时才能设置 BigFontFileName，
                            // 因此清空时必须先清 BigFontFileName 再清 FileName，否则 eInvalidInput。
                            style.BigFontFileName = string.Empty;
                            style.FileName = string.Empty;
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
                            // 无条件清空 FontDescriptor，确保不残留任何 TrueType 信息。
                            // 不依赖 TypeFace 读回值判断 — AutoCAD 内部状态可能不一致。
                            // 必须先清 Font 再设 FileName，否则设置 Font 可能重置 FileName。
                            style.Font = new FontDescriptor("", false, false, 0, 0);
                            style.FileName = mainFont;

                            // 始终重建 BigFont 状态，避免旧值残留或与新主字体不匹配
                            style.BigFontFileName = bigFontValid ? bigFont : string.Empty;

                            changed = true;
                        }
                    }
                }
                // 单独处理仅 BigFont 缺失的情况（主字体正常，常见于中文 SHX 字体）
                // TrueType 样式不支持大字体，跳过；FileName 必须有效才能设置 BigFontFileName
                else if (!missing.IsTrueType && missing.IsBigFontMissing
                         && !string.IsNullOrEmpty(style.FileName))
                {
                    style.BigFontFileName = bigFontValid ? bigFont : string.Empty;
                    changed = true;
                }

                if (changed) replaceCount++;
            }
            catch (Exception ex)
            {
                log.Error($"替换样式 {id} 的字体失败（已跳过）", ex);
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
        IReadOnlyList<StyleFontReplacement> replacements,
        FontDetectionContext context)
    {
        if (replacements.Count == 0) return 0;

        var log = LogService.Instance;
        int replaceCount = 0;

        var map = new Dictionary<string, StyleFontReplacement>(replacements.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in replacements)
            map.TryAdd(r.StyleName, r);

        using var tr = context.Db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(context.Db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!map.TryGetValue(style.Name, out var replacement)) continue;

                // ShapeFile 样式用于复杂线型（ltypeshp.shx 等），替换会破坏线型结构
                if (style.IsShapeFile) continue;

                bool changed = false;
                style.UpgradeOpen();

                if (!string.IsNullOrEmpty(replacement.MainFontReplacement))
                {
                    if (replacement.IsTrueType)
                    {
                        if (!FontDetector.IsTrueTypeFontAvailable(replacement.MainFontReplacement, context))
                        {
                            log.Warning($"手动替换: 样式 '{replacement.StyleName}' 的 TrueType 替换字体 '{replacement.MainFontReplacement}' 不可用，跳过");
                        }
                        else
                        {
                            // FontDescriptor 要求字族名，去除可能的文件扩展名
                            var fontFamily = NormalizeTrueTypeName(replacement.MainFontReplacement, context);
                            var (charset, pitch) = FontDetector.GetTrueTypeFontMetrics(fontFamily, context);
                            // 清空顺序: 先 BigFont 再 FileName，避免 eInvalidInput
                            style.BigFontFileName = string.Empty;
                            style.FileName = string.Empty;
                            style.Font = new FontDescriptor(fontFamily, false, false, charset, pitch);
                            changed = true;
                        }
                    }
                    else
                    {
                        if (!FontDetector.IsShxFontAvailable(replacement.MainFontReplacement, context))
                        {
                            log.Warning($"手动替换: 样式 '{replacement.StyleName}' 的 SHX 替换字体 '{replacement.MainFontReplacement}' 不可用，跳过");
                        }
                        else
                        {
                            // 无条件清空 FontDescriptor，确保不残留任何 TrueType 信息
                            style.Font = new FontDescriptor("", false, false, 0, 0);
                            style.FileName = replacement.MainFontReplacement;

                            // 始终重建 BigFont 状态，避免旧值残留或与新主字体不匹配
                            if (!string.IsNullOrEmpty(replacement.BigFontReplacement)
                                && FontDetector.IsShxFontAvailable(replacement.BigFontReplacement, context))
                            {
                                style.BigFontFileName = replacement.BigFontReplacement;
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(replacement.BigFontReplacement))
                                    log.Warning($"手动替换: 样式 '{replacement.StyleName}' 的大字体替换字体 '{replacement.BigFontReplacement}' 不可用，已清空");
                                style.BigFontFileName = string.Empty;
                            }

                            changed = true;
                        }
                    }
                }

                if (changed) replaceCount++;
            }
            catch (Exception ex)
            {
                log.Error($"手动替换样式 {id} 的字体失败（已跳过）", ex);
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
    public static int CleanupStaleShxReferences(FontDetectionContext context)
    {
        var log = LogService.Instance;
        int cleaned = 0;

        // 系统字体索引未就绪时跳过清理，避免误判
        if (!FontDetector.IsSystemFontIndexReady)
        {
            log.Warning("[清理] 系统字体索引尚未就绪，跳过残留 SHX 清理以避免误操作");
            return 0;
        }

        using var tr = context.Db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(context.Db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);

                // 隔离 style.Font 访问 — 损坏的描述符不应阻断后续样式处理
                FontDescriptor? safeFont = null;
                try { safeFont = style.Font; }
                catch (Exception fontEx)
                {
                    log.Warning($"[清理] 样式 '{style.Name}' 的 TrueType 描述符损坏，已跳过: {fontEx.Message}");
                    continue;
                }
                var font = safeFont.Value;

                // 仅处理有 TrueType 字族名的样式
                if (string.IsNullOrEmpty(font.TypeFace)) continue;

                // TrueType 必须已安装（通过系统字体索引或 FindFile 双重验证）
                if (!FontDetector.IsSystemFont(font.TypeFace)
                    && !FontDetector.IsTrueTypeFontAvailable(font.TypeFace, context))
                    continue;

                var fileName = style.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                // FileName 是 TrueType 文件 → 不需要清理
                if (fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 复用 FontDetector 的缓存查找，避免直接调用 FindFile 引发异常风暴
                if (FontDetector.IsShxFontAvailable(fileName, context))
                    continue; // SHX 存在，无需清理

                // TrueType 可用 + SHX 缺失 → 清除残留 SHX 引用
                style.UpgradeOpen();
                log.Info($"[清理] 样式='{style.Name}' TrueType='{font.TypeFace}' 清除残留 FileName='{fileName}' BigFont='{style.BigFontFileName}'");
                // 清空顺序: 先 BigFont 再 FileName，避免 eInvalidInput
                style.BigFontFileName = string.Empty;
                style.FileName = string.Empty;
                cleaned++;
            }
            catch (Exception ex)
            {
                log.Warning($"[清理] 处理样式 {id} 时出错（已跳过）: {ex.Message}");
            }
        }

        tr.Commit();
        return cleaned;
    }

    /// <summary>
    /// 将 TrueType 字体名归一化为字族名。
    /// FontDescriptor 和 GDI 查询要求纯字族名（如 "SimSun"），
    /// 不能包含文件扩展名（如 "simsun.ttc"），否则 AutoCAD 找不到字体。
    /// 优先通过 GlyphTypeface 解析字体文件内部的真实字族名，
    /// 避免文件名与字族名不一致的问题（如 FZYTK.TTF → "方正姚体"）。
    /// </summary>
    private static string NormalizeTrueTypeName(string name, FontDetectionContext context)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        if (name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // 通过 AutoCAD 搜索路径定位字体文件，解析内部真实字族名
                string path = HostApplicationServices.Current.FindFile(
                    name, context.Db, FindFileHint.TrueTypeFontFile);
                if (!string.IsNullOrEmpty(path))
                {
                    var glyph = new GlyphTypeface(new Uri(path));
                    var familyName = glyph.FamilyNames.Values.FirstOrDefault();
                    if (!string.IsNullOrEmpty(familyName))
                        return familyName;
                }
            }
            catch
            {
                // FindFile 或 GlyphTypeface 解析失败 — 降级为扩展名截断
            }

            return Path.GetFileNameWithoutExtension(name);
        }

        return name;
    }

    }
