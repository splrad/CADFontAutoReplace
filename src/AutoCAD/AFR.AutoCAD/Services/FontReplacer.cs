using System.IO;
using System.Windows.Media;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using AFR.FontMapping;
using AFR.Models;


namespace AFR.Services;

/// <summary>
/// 使用配置的备用字体替换文字样式表中的缺失字体。
/// <para>
/// 只改已确认缺失的样式；替换字体不可用时跳过并提示。
/// </para>
/// </summary>
internal static class FontReplacer
{
    /// <summary>
    /// 按全局配置替换缺失字体。
    /// <para>
    /// TrueType、SHX 主字体、SHX 大字体分别使用对应配置值。
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

        // 写回前先校验替换字体，避免把样式写成不可用值。
        bool mainFontValid = !string.IsNullOrEmpty(mainFont)
            && FontDetector.IsShxFontAvailable(mainFont, context);
        bool bigFontValid = !string.IsNullOrEmpty(bigFont)
            && FontDetector.IsShxFontAvailable(bigFont, context)
            && !FontDetector.IsShxTypeMismatch(bigFont, context, expectBigFont: true);
        bool trueTypeFontValid = !string.IsNullOrEmpty(trueTypeFont)
            && FontDetector.IsTrueTypeFontAvailable(FontRedirectResolver.StripLeadingAtPrefix(trueTypeFont), context);

        if (!string.IsNullOrEmpty(mainFont) && !mainFontValid)
            log.Warning($"SHX 替换字体 '{mainFont}' 不可用，已跳过，请执行 AFR 重新配置");
        if (!string.IsNullOrEmpty(bigFont) && !bigFontValid)
            log.Warning($"大字体 '{bigFont}' 不可用或类型不匹配，已跳过，请执行 AFR 重新配置");
        if (!string.IsNullOrEmpty(trueTypeFont) && !trueTypeFontValid)
            log.Warning($"TrueType 替换字体 '{trueTypeFont}' 不可用，已跳过，请执行 AFR 重新配置");

        DiagnosticLogger.LogPreValidation(mainFont ?? "", "SHX主字体", mainFontValid);
        DiagnosticLogger.LogPreValidation(bigFont ?? "", "大字体", bigFontValid);
        DiagnosticLogger.LogPreValidation(trueTypeFont ?? "", "TrueType", trueTypeFontValid);

        // 按样式名快速定位原始缺失记录。
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

                // ShapeFile 属于复杂线型，不做字体替换。
                if (style.IsShapeFile)
                {
                    DiagnosticLogger.LogSkipped(style.Name, "IsShapeFile=true");
                    continue;
                }

                bool changed = false;
                style.UpgradeOpen();

                if (missing.IsMainFontMissing)
                {
                    if (missing.IsTrueType)
                    {
                        // @TrueType 只能写入预解析出的可用 @face。
                        bool preserveAtPrefix = ShouldPreserveTrueTypeAtPrefix(missing);
                        bool canReplaceTrueType = preserveAtPrefix
                            ? TrueTypeFontAvailabilityIndex.TryGetResolvedAtTrueTypeFont(out _, out _)
                            : trueTypeFontValid;
                        if (canReplaceTrueType)
                        {
                            if (!TryBuildTrueTypeWriteFace(
                                trueTypeFont!,
                                preserveAtPrefix,
                                context,
                                out string typeFaceToWrite,
                                out string metricsFontName))
                            {
                                DiagnosticLogger.LogSkipped(style.Name, "@TrueType未找到可用兜底字体");
                                continue;
                            }

                            var (charset, pitch) = FontDetector.GetTrueTypeFontMetrics(metricsFontName, context);

                            // 清空顺序关键：先 BigFont 再 FileName，避免 AutoCAD 抛 eInvalidInput。
                            style.BigFontFileName = string.Empty;
                            style.FileName = string.Empty;
                            style.Font = new FontDescriptor(typeFaceToWrite, false, false, charset, pitch);

                            DiagnosticLogger.LogReplacement(style.Name, "Font.TypeFace",
                                missing.TypeFace, typeFaceToWrite);

                            changed = true;
                        }
                        else
                        {
                            DiagnosticLogger.LogSkipped(
                                style.Name,
                                preserveAtPrefix ? "@TrueType替换字体不可用" : "TrueType替换字体不可用");
                        }
                    }
                    else
                    {
                        // SHX 样式只写入已通过校验的 SHX 文件名。
                        if (mainFontValid)
                        {
                            // 先清 FontDescriptor 再设 FileName，避免残留 TrueType 状态。
                            style.Font = new FontDescriptor("", false, false, 0, 0);
                            style.FileName = mainFont;

                            DiagnosticLogger.LogReplacement(style.Name, "FileName",
                                missing.FileName, mainFont ?? "");

                            changed = true;
                        }
                        else
                        {
                            DiagnosticLogger.LogSkipped(style.Name, "SHX替换字体不可用");
                        }
                    }
                }
                // BigFont 只能写入非 TrueType 且 FileName 有效的样式。
                if (!missing.IsTrueType && missing.IsBigFontMissing)
                {
                    if (!bigFontValid)
                    {
                        DiagnosticLogger.LogSkipped(style.Name, "大字体替换字体不可用");
                    }
                    else if (missing.IsMainFontMissing && !mainFontValid)
                    {
                        DiagnosticLogger.LogSkipped(style.Name, "SHX主字体未替换，跳过大字体替换");
                    }
                    else if (string.IsNullOrEmpty(style.FileName))
                    {
                        DiagnosticLogger.LogSkipped(style.Name, "FileName为空，跳过大字体替换");
                    }
                    else
                    {
                        style.BigFontFileName = bigFont;
                        DiagnosticLogger.LogReplacement(style.Name, "BigFontFileName",
                            missing.BigFontFileName, bigFont ?? "");
                        changed = true;
                    }
                }

                if (changed) replaceCount++;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Fail(
                    "FontReplacer",
                    "ReplaceMissingFonts",
                    "替换样式字体失败，已跳过",
                    ex,
                    new Dictionary<string, object?> { ["objectId"] = id.ToString() });
            }
        }

        tr.Commit();
        return replaceCount;
    }

    /// <summary>
    /// AFRLOG 手动替换：按样式名写入指定字体。
    /// <para>
    /// 只影响当前图纸样式表，不修改注册表全局配置。
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
            map.TryAdd(r.StyleName, r);

        using var tr = context.Db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(context.Db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!map.TryGetValue(style.Name, out var replacement)) continue;

                // ShapeFile 属于复杂线型，不做字体替换。
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
                        bool preserveAtPrefix = replacement.PreserveTrueTypeAtPrefix
                                                || FontRedirectResolver.HasAtPrefix(replacement.MainFontReplacement);
                        string replacementLookup = FontRedirectResolver.StripLeadingAtPrefix(replacement.MainFontReplacement);
                        bool replacementAvailable = preserveAtPrefix
                            ? TrueTypeFontAvailabilityIndex.TryGetResolvedAtTrueTypeFont(out _, out _)
                            : FontDetector.IsTrueTypeFontAvailable(replacementLookup, context);
                        if (!replacementAvailable)
                        {
                            log.Warning($"样式 '{replacement.StyleName}': 字体 '{replacement.MainFontReplacement}' 不可用，已跳过");
                        }
                        else
                        {
                            string requestedTrueTypeFont = replacement.MainFontReplacement;
                            // FontDescriptor 要求字族名，不能写 .ttf/.ttc 文件名。
                            if (!TryBuildTrueTypeWriteFace(
                                requestedTrueTypeFont,
                                replacement.PreserveTrueTypeAtPrefix,
                                context,
                                out string fontFamily,
                                out string metricsFontName))
                            {
                                log.Warning($"样式 '{replacement.StyleName}': @TrueType 未找到可用兜底字体，已跳过");
                                DiagnosticLogger.LogSkipped(replacement.StyleName, "@TrueType手动替换无可用兜底字体");
                                continue;
                            }

                            var (charset, pitch) = FontDetector.GetTrueTypeFontMetrics(metricsFontName, context);
                            // 清空顺序：先 BigFont 再 FileName，避免 eInvalidInput。
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
                            // 清掉 TrueType 状态后再写 SHX。
                            style.Font = new FontDescriptor("", false, false, 0, 0);
                            style.FileName = replacement.MainFontReplacement;

                            // 主字体变更时同步重建 BigFont，避免旧值残留。
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
                // 仅 BigFont 变更时，与自动替换的 BigFont-only 分支保持一致。
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
                DiagnosticLogger.Fail(
                    "FontReplacer",
                    "ReplaceByStyleMapping",
                    "手动替换样式字体失败，已跳过",
                    ex,
                    new Dictionary<string, object?> { ["objectId"] = id.ToString() });
            }
        }

        tr.Commit();
        return replaceCount;
    }

    /// <summary>
    /// 清理样式表中 TrueType 可用但 SHX 引用缺失的残留引用。
    /// <para>
    /// 这类残留会让 Hook 重定向无用 SHX，导致 ST 认为样式被改动；清掉 FileName 不影响 TrueType 渲染。
    /// </para>
    /// </summary>
    /// <returns>被清理的样式数量。</returns>
    public static int CleanupStaleShxReferences(FontDetectionContext context)
    {
        var log = LogService.Instance;
        int cleaned = 0;

        // 系统字体索引未就绪时跳过，避免误清理。
        if (!FontDetector.IsSystemFontIndexReady)
        {
            DiagnosticLogger.Skip(
                "FontReplacer",
                "CleanupStaleShxReferences",
                "系统字体索引尚未就绪，跳过残留 SHX 清理");
            return 0;
        }

        using var tr = context.Db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(context.Db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);

                // style.Font 可能损坏；隔离读取，避免阻断后续样式。
                FontDescriptor? safeFont = null;
                try { safeFont = style.Font; }
                catch (Exception fontEx)
                {
                    DiagnosticLogger.Skip(
                        "FontReplacer",
                        "CleanupStaleShxReferences",
                        "TrueType 描述符损坏，已跳过清理",
                        new Dictionary<string, object?>
                        {
                            ["styleName"] = style.Name,
                            ["error"] = fontEx.Message
                        });
                    continue;
                }
                var font = safeFont.Value;

                // 只处理 TrueType 可渲染、但残留缺失 SHX 的样式。
                if (string.IsNullOrEmpty(font.TypeFace)) continue;

                if (!FontDetector.IsSystemFont(font.TypeFace)
                    && !FontDetector.IsTrueTypeFontAvailable(font.TypeFace, context))
                    continue;

                var fileName = style.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                if (FontDetector.IsTrueTypeFontFile(fileName))
                    continue;

                if (FontDetector.IsShxFontAvailable(fileName, context))
                    continue; // SHX 存在，无需清理

                style.UpgradeOpen();
                DiagnosticLogger.Ok(
                    "FontReplacer",
                    "CleanupStaleShxReferences",
                    "样式残留 SHX 引用已清理",
                    new Dictionary<string, object?>
                    {
                        ["styleName"] = style.Name,
                        ["typeFace"] = font.TypeFace,
                        ["fileName"] = fileName,
                        ["bigFontFileName"] = style.BigFontFileName
                    });
                // 清空顺序：先 BigFont 再 FileName，避免 eInvalidInput。
                style.BigFontFileName = string.Empty;
                style.FileName = string.Empty;
                cleaned++;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Fail(
                    "FontReplacer",
                    "CleanupStaleShxReferences",
                    "处理样式时出错，已跳过",
                    ex,
                    new Dictionary<string, object?> { ["objectId"] = id.ToString() });
            }
        }

        tr.Commit();
        return cleaned;
    }

    /// <summary>
    /// 将 TrueType 字体名归一化为字族名。
    /// FontDescriptor 和 GDI 查询不能使用 .ttf/.ttc 文件名；文件名配置会解析为内部字族名。
    /// </summary>
    private static string NormalizeTrueTypeName(string name, FontDetectionContext context)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        if (FontDetector.IsTrueTypeFontFile(name))
        {
            try
            {
                // 通过 CAD 搜索路径定位字体文件，再解析内部字族名。
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
                // 解析失败时退回到去扩展名。
            }

            var fallback = Path.GetFileNameWithoutExtension(name);
            DiagnosticLogger.LogNormalize(name, $"{fallback} (降级)");
            return fallback;
        }

        return name;
    }

    private static bool ShouldPreserveTrueTypeAtPrefix(FontCheckResult missing)
        => missing.IsTrueType
           && (FontRedirectResolver.HasAtPrefix(missing.TypeFace)
               || FontRedirectResolver.HasAtPrefix(missing.FileName));

    private static bool TryBuildTrueTypeWriteFace(
        string requestedTrueTypeFont,
        bool preserveAtPrefix,
        FontDetectionContext context,
        out string typeFace,
        out string metricsFontName)
    {
        typeFace = string.Empty;
        string requestedBase = FontRedirectResolver.StripLeadingAtPrefix(requestedTrueTypeFont);
        metricsFontName = NormalizeTrueTypeName(requestedBase, context).TrimStart('@');
        bool writeAtPrefix = preserveAtPrefix || FontRedirectResolver.HasAtPrefix(requestedTrueTypeFont);
        string atResolutionSource = "Configured";

        if (writeAtPrefix)
        {
            if (!TrueTypeFontAvailabilityIndex.TryGetResolvedAtTrueTypeFont(
                    out string resolvedAtBaseFont,
                    out atResolutionSource))
            {
                DiagnosticLogger.Fail(
                    "FontReplacer",
                    "NormalizeTrueTypeReplacement",
                    "@TrueType 未找到可用的 @face 兜底字体",
                    fields: new Dictionary<string, object?>
                    {
                        ["requestedTrueTypeFont"] = requestedTrueTypeFont,
                        ["preserveAtPrefix"] = true
                    });
                return false;
            }

            metricsFontName = NormalizeTrueTypeName(resolvedAtBaseFont, context).TrimStart('@');
            typeFace = "@" + metricsFontName;
        }
        else
        {
            typeFace = metricsFontName;
        }

        DiagnosticLogger.Ok(
            "FontReplacer",
            "NormalizeTrueTypeReplacement",
            "TrueType 替换字体已解析为 TypeFace",
            new Dictionary<string, object?>
            {
                ["requestedTrueTypeFont"] = requestedTrueTypeFont,
                ["typeFace"] = typeFace,
                ["metricsFontName"] = metricsFontName,
                ["preserveAtPrefix"] = writeAtPrefix,
                ["atResolutionSource"] = atResolutionSource,
                ["configuredAtCapable"] = TrueTypeFontAvailabilityIndex.IsConfiguredTrueTypeAtCapable
            });
        return true;
    }
}
