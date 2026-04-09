using System.IO;
using System.Windows.Media;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using AFR.Models;


namespace AFR.Services;

/// <summary>
/// 使用配置的备用字体替换文字样式表中的缺失字体。
/// <para>
/// 仅修改已确认缺失的样式，正常字体不受影响。
/// 替换前会预校验替换字体的可用性，不可用的替换字体会被跳过并警告用户。
/// 支持两种替换模式：全局替换（<see cref="ReplaceMissingFonts"/>）和按样式逐一替换（<see cref="ReplaceByStyleMapping"/>）。
/// </para>
/// </summary>
internal static class FontReplacer
{
    /// <summary>
    /// 使用全局配置的备用字体替换所有缺失字体。
    /// <para>
    /// 替换策略：TrueType 缺失 → 用 trueTypeFont 替换；
    /// SHX 主字体缺失 → 用 mainFont 替换；SHX 大字体缺失 → 用 bigFont 替换。
    /// </para>
    /// </summary>
    /// <param name="missingFonts">缺失字体检查结果列表。</param>
    /// <param name="mainFont">SHX 主字体替换名称。</param>
    /// <param name="bigFont">SHX 大字体替换名称。</param>
    /// <param name="trueTypeFont">TrueType 字体替换名称。</param>
    /// <param name="context">字体检测上下文。</param>
    /// <returns>被成功修改的样式数量。</returns>
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
            && FontDetector.IsShxFontAvailable(bigFont, context)
            && !FontDetector.IsShxTypeMismatch(bigFont, context, expectBigFont: true);
        bool trueTypeFontValid = !string.IsNullOrEmpty(trueTypeFont)
            && FontDetector.IsTrueTypeFontAvailable(trueTypeFont, context);

        if (!string.IsNullOrEmpty(mainFont) && !mainFontValid)
            log.Warning($"SHX 替换字体 '{mainFont}' 不可用，已跳过，请执行 AFR 重新配置");
        if (!string.IsNullOrEmpty(bigFont) && !bigFontValid)
            log.Warning($"大字体 '{bigFont}' 不可用或类型不匹配，已跳过，请执行 AFR 重新配置");
        if (!string.IsNullOrEmpty(trueTypeFont) && !trueTypeFontValid)
            log.Warning($"TrueType 替换字体 '{trueTypeFont}' 不可用，已跳过，请执行 AFR 重新配置");

        DiagnosticLogger.LogPreValidation(mainFont ?? "", "SHX主字体", mainFontValid);
        DiagnosticLogger.LogPreValidation(bigFont ?? "", "大字体", bigFontValid);
        DiagnosticLogger.LogPreValidation(trueTypeFont ?? "", "TrueType", trueTypeFontValid);

        // FontDescriptor 和 GDI 查询要求字族名（如 "SimSun"），而非文件名（如 "simsun.ttc"）
        if (trueTypeFontValid)
            trueTypeFont = NormalizeTrueTypeName(trueTypeFont!, context);

        // 预构建字典—O(1)查找替代线性搜索
        var missingMap = new Dictionary<string, FontCheckResult>(missingFonts.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < missingFonts.Count; i++)
        {
            if (!missingMap.ContainsKey(missingFonts[i].StyleName)) missingMap.Add(missingFonts[i].StyleName, missingFonts[i]);
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
                if (style.IsShapeFile)
                {
                    DiagnosticLogger.LogSkipped(style.Name, "IsShapeFile=true");
                    continue;
                }

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
                            var (charset, pitch) = FontDetector.GetTrueTypeFontMetrics(trueTypeFont!, context);

                            // 先清除 SHX 引用，再设置 TrueType
                            // 顺序关键: AutoCAD 要求 FileName 为有效 SHX 时才能设置 BigFontFileName，
                            // 因此清空时必须先清 BigFontFileName 再清 FileName，否则 eInvalidInput。
                            style.BigFontFileName = string.Empty;
                            style.FileName = string.Empty;
                            style.Font = new FontDescriptor(trueTypeFont, false, false, charset, pitch);

                            DiagnosticLogger.LogReplacement(style.Name, "Font.TypeFace",
                                missing.TypeFace, trueTypeFont ?? "");

                            changed = true;
                        }
                        else
                        {
                            DiagnosticLogger.LogSkipped(style.Name, "TrueType替换字体不可用");
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

                            DiagnosticLogger.LogReplacement(style.Name, "FileName",
                                missing.FileName, mainFont ?? "");
                            DiagnosticLogger.LogReplacement(style.Name, "BigFontFileName",
                                missing.BigFontFileName, bigFontValid ? bigFont ?? "" : "");

                            changed = true;
                        }
                        else
                        {
                            DiagnosticLogger.LogSkipped(style.Name, "SHX替换字体不可用");
                        }
                    }
                }
                // 单独处理仅 BigFont 缺失的情况（主字体正常，常见于中文 SHX 字体）
                // TrueType 样式不支持大字体，跳过；FileName 必须有效才能设置 BigFontFileName
                else if (!missing.IsTrueType && missing.IsBigFontMissing
                         && !string.IsNullOrEmpty(style.FileName))
                {
                    style.BigFontFileName = bigFontValid ? bigFont : string.Empty;
                    DiagnosticLogger.LogReplacement(style.Name, "BigFontFileName",
                        missing.BigFontFileName, bigFontValid ? bigFont ?? "" : "");
                    changed = true;
                }

                if (changed) replaceCount++;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"替换样式 {id} 的字体失败（已跳过）", ex);
            }
        }

        tr.Commit();
        return replaceCount;
    }

    /// <summary>
    /// 按样式名称与指定的替换字体进行逐一替换（AFRLOG 手动替换模式）。
    /// <para>
    /// 用于用户在日志界面中手动为每个样式指定不同的替换字体。
    /// 仅影响当前图纸中的样式表，不修改注册表全局配置。
    /// </para>
    /// </summary>
    /// <param name="replacements">每个样式的替换规格列表。</param>
    /// <param name="context">字体检测上下文。</param>
    /// <returns>被成功修改的样式数量。</returns>
    public static int ReplaceByStyleMapping(
        IReadOnlyList<StyleFontReplacement> replacements,
        FontDetectionContext context)
    {
        if (replacements.Count == 0) return 0;

        var log = LogService.Instance;
        int replaceCount = 0;

        var map = new Dictionary<string, StyleFontReplacement>(replacements.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in replacements)
            if (!map.ContainsKey(r.StyleName)) map.Add(r.StyleName, r);

        using var tr = context.Db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(context.Db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!map.TryGetValue(style.Name, out var replacement)) continue;

                // ShapeFile 样式用于复杂线型（ltypeshp.shx 等），替换会破坏线型结构
                if (style.IsShapeFile)
                {
                    DiagnosticLogger.LogSkipped(style.Name, "IsShapeFile=true (手动)");
                    continue;
                }

                bool changed = false;
                style.UpgradeOpen();

                if (!string.IsNullOrEmpty(replacement.MainFontReplacement))
                {
                    if (replacement.IsTrueType)
                    {
                        if (!FontDetector.IsTrueTypeFontAvailable(replacement.MainFontReplacement, context))
                        {
                            log.Warning($"样式 '{replacement.StyleName}': 字体 '{replacement.MainFontReplacement}' 不可用，已跳过");
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
                            log.Warning($"样式 '{replacement.StyleName}': 字体 '{replacement.MainFontReplacement}' 不可用，已跳过");
                        }
                        else
                        {
                            // 无条件清空 FontDescriptor，确保不残留任何 TrueType 信息
                            style.Font = new FontDescriptor("", false, false, 0, 0);
                            style.FileName = replacement.MainFontReplacement;

                            // 始终重建 BigFont 状态，避免旧值残留或与新主字体不匹配
                            if (!string.IsNullOrEmpty(replacement.BigFontReplacement)
                                && FontDetector.IsShxFontAvailable(replacement.BigFontReplacement, context)
                                && !FontDetector.IsShxTypeMismatch(replacement.BigFontReplacement, context, expectBigFont: true))
                            {
                                style.BigFontFileName = replacement.BigFontReplacement;
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(replacement.BigFontReplacement))
                                    log.Warning($"样式 '{replacement.StyleName}': 大字体 '{replacement.BigFontReplacement}' 不可用，已清空");
                                style.BigFontFileName = string.Empty;
                            }

                            changed = true;
                        }
                    }
                }
                // 仅大字体需要替换（主字体未变更）— 与 ReplaceMissingFonts 的 BigFont-only 分支对齐
                else if (!replacement.IsTrueType
                         && !string.IsNullOrEmpty(replacement.BigFontReplacement)
                         && !string.IsNullOrEmpty(style.FileName))
                {
                    if (FontDetector.IsShxFontAvailable(replacement.BigFontReplacement, context)
                        && !FontDetector.IsShxTypeMismatch(replacement.BigFontReplacement, context, expectBigFont: true))
                    {
                        style.BigFontFileName = replacement.BigFontReplacement;
                        changed = true;
                    }
                    else
                    {
                        log.Warning($"样式 '{replacement.StyleName}': 大字体 '{replacement.BigFontReplacement}' 不可用或类型不匹配，已跳过");
                    }
                }

                if (changed) replaceCount++;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError($"手动替换样式 {id} 的字体失败（已跳过）", ex);
            }
        }

        tr.Commit();
        return replaceCount;
    }

    /// <summary>
    /// 清理样式表中 TrueType 可用但 SHX 引用缺失的残留引用。
    /// <para>
    /// 当一个样式同时有 TypeFace（已安装的 TrueType）和 FileName（缺失的 SHX）时，
    /// AutoCAD 使用 TrueType 渲染，SHX 引用实际无用。但 Hook 会在加载阶段将缺失 SHX
    /// 重定向到替换字体，导致内部缓存与 DWG 实际数据不一致 → ST 弹出"已修改"提示。
    /// 清除 FileName 可消除这种不一致，同时不影响渲染（TrueType 仍可用）。
    /// </para>
    /// </summary>
    /// <returns>被清理的样式数量。</returns>
    public static int CleanupStaleShxReferences(FontDetectionContext context)
    {
        var log = LogService.Instance;
        int cleaned = 0;

        // 系统字体索引未就绪时跳过清理，避免误判
        if (!FontDetector.IsSystemFontIndexReady)
        {
            DiagnosticLogger.Log("清理", "系统字体索引尚未就绪，跳过残留 SHX 清理");
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
                    DiagnosticLogger.Log("清理", $"样式 '{style.Name}' 的 TrueType 描述符损坏，已跳过: {fontEx.Message}");
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
                if (FontDetector.IsTrueTypeFontFile(fileName))
                    continue;

                // 复用 FontDetector 的缓存查找，避免直接调用 FindFile 引发异常风暴
                if (FontDetector.IsShxFontAvailable(fileName, context))
                    continue; // SHX 存在，无需清理

                // TrueType 可用 + SHX 缺失 → 清除残留 SHX 引用
                style.UpgradeOpen();
                DiagnosticLogger.Log("清理", $"样式='{style.Name}' TrueType='{font.TypeFace}' 清除残留 FileName='{fileName}' BigFont='{style.BigFontFileName}'");
                // 清空顺序: 先 BigFont 再 FileName，避免 eInvalidInput
                style.BigFontFileName = string.Empty;
                style.FileName = string.Empty;
                cleaned++;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("清理", $"处理样式 {id} 时出错（已跳过）: {ex.Message}");
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

        if (FontDetector.IsTrueTypeFontFile(name))
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
                    {
                        DiagnosticLogger.LogNormalize(name, familyName);
                        return familyName;
                    }
                }
            }
            catch
            {
                // FindFile 或 GlyphTypeface 解析失败 — 降级为扩展名截断
            }

            var fallback = Path.GetFileNameWithoutExtension(name);
            DiagnosticLogger.LogNormalize(name, $"{fallback} (降级)");
            return fallback;
        }

        return name;
    }

    }
