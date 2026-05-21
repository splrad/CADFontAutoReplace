using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.FontMapping;
using AFR.Models;
using AFR.Services;

namespace AFR.Hosting;

/// <summary>
/// 统一执行控制器，协调字体检测与替换的完整流程。
/// <para>
/// 处理三种触发来源：Startup（插件启动）、Command（用户命令）、DocumentCreated（新建文档）。
/// 内置重复执行防护（同一文档不会重复处理）和 IsInitialized 门控（未配置替换字体时跳过）。
/// </para>
/// </summary>
internal sealed class ExecutionController
{
    private static readonly Lazy<ExecutionController> _instance = new(() => new ExecutionController());
    /// <summary>获取 ExecutionController 的全局唯一实例。</summary>
    public static ExecutionController Instance => _instance.Value;

    private ExecutionController() { }

    /// <summary>
    /// 对指定文档执行字体检测与替换的完整流程。
    /// <para>
    /// 执行阶段：检测缺失字体 → 替换 → 二次验证 → MText 内联字体扫描 → 统计输出。
    /// 遵守 IsInitialized 门控（未配置则跳过）和重复执行防护（同文档只执行一次）。
    /// </para>
    /// </summary>
    /// <param name="doc">要处理的 AutoCAD 文档。</param>
    /// <param name="triggerSource">触发来源标识，用于诊断日志。</param>
    public void Execute(Document doc, string triggerSource)
    {
        if (doc == null || doc.IsDisposed) return;

        using var dialogSystemVariables = CadDialogSystemVariableScope.Capture();
        var log = LogService.Instance;
        var config = ConfigService.Instance;
        bool summarized = false;

        try
        {
            // 重复执行防护
            var contextMgr = DocumentContextManager.Instance;
            if (contextMgr.HasExecuted(doc)) return;

            // 门控: 未配置替换字体时跳过
            if (!config.IsInitialized)
            {
                log.Info("请输入 AFR 命令配置替换字体。");
                return;
            }

            // 获取文档写入锁 — 修改样式表需要写锁
            using (doc.LockDocument())
            {
                bool needsVisualRegen = false;
                // 创建独立的字体检测上下文 — 缓存生命周期与本次执行绑定，结束后由 GC 自动回收
                var context = new FontDetectionContext(doc.Database);
                FontRuntimeMappingStore.Clear();
                LdFileHook.ClearRegisteredRedirects();
                StyleTextStyleHook.ReplaceStyleRuntimeFontMappings(Array.Empty<RuntimeFontMappingRecord>());

                DiagnosticLogger.BeginDocument(doc.Name, config.MainFont, config.BigFont, config.TrueTypeFont);

                // 第一阶段: 检测缺失字体（读取样式表原始状态，判断哪些字体在系统中不可用）
                DiagnosticLogger.BeginPhase("检测缺失字体");
                var missingFonts = FontDetector.DetectMissingFonts(context);

                // 存储检测结果供 AFRLOG 命令使用
                contextMgr.StoreDetectionResults(doc, missingFonts);
                DiagnosticLogger.EndPhase($"缺失: {missingFonts.Count}个");

                // 第二阶段: 恢复样式表永久替换。FontDetector 已排除 @ 前缀样式字体，
                // 因此这里仅会写回普通缺失字体；@ 字体保留给 StyleTextStyleHook 做临时映射。
                int replacedStyleCount = 0;
                if (missingFonts.Count > 0)
                {
                    DiagnosticLogger.BeginPhase("替换样式表缺失字体");
                    replacedStyleCount = FontReplacer.ReplaceMissingFonts(
                        missingFonts,
                        config.MainFont,
                        config.BigFont,
                        config.TrueTypeFont,
                        context);
                    DiagnosticLogger.EndPhase($"替换: {replacedStyleCount}项");

                    if (replacedStyleCount > 0)
                    {
                        needsVisualRegen = true;
                    }
                }

                // 第三阶段: 二次检测替换后的样式表状态，供 AFRLOG 标记仍缺失样式。
                DiagnosticLogger.BeginPhase("替换后二次检测");
                var postContext = new FontDetectionContext(doc.Database);
                var stillMissing = FontDetector.DetectMissingFonts(postContext);
                contextMgr.StoreStillMissingResults(doc, stillMissing);
                DiagnosticLogger.EndPhase($"仍缺失: {stillMissing.Count}个");

                // 第四阶段: 仅登记样式表 @ 前缀缺失字体的临时运行时映射。
                var runtimeFontMappings = FontDetector.CollectRuntimeFontMappings(postContext, config.TrueTypeFont);
                List<RuntimeFontMappingRecord> actualStyleRuntimeMappings = [];
                if (runtimeFontMappings.Count > 0)
                {
                    DiagnosticLogger.BeginPhase("样式表运行时映射");
                    ActivateStyleRuntimeFontMappings(runtimeFontMappings);
                    ForceLoadStyleRuntimeMappings(doc.Database, runtimeFontMappings);

                    // 触发型 Regen: 这里必须立即触发 AcGiTextStyle::loadStyleRec，
                    // 让样式表 @ 字体映射在 StyleTextStyleHook 中命中，不能与最终视觉刷新合并。
                    doc.Editor.Regen();
                    needsVisualRegen = false;

                    actualStyleRuntimeMappings = FontRuntimeMappingStore.GetStyleMappings();
                    contextMgr.StoreRuntimeFontMappingResults(doc, actualStyleRuntimeMappings);
                    DiagnosticLogger.EndPhase($"登记: {runtimeFontMappings.Count}项, 命中: {actualStyleRuntimeMappings.Count}项");
                }
                else
                {
                    contextMgr.StoreRuntimeFontMappingResults(doc, actualStyleRuntimeMappings);
                }

                // 第五阶段: 只读扫描 MText 内联字体；实际映射结果由 MText Hook 自身记录。
                // 不改写 MText.Contents，避免接管 CAD 原生内联格式解析。
                DiagnosticLogger.BeginPhase("扫描MText内联字体");
                MTextInlineFontScanResult inlineScanResult;
                List<InlineFontFixRecord> inlineFixResults;
                string hookStatsBeforeInlineScan;
                string hookStatsAfterInlineRegen;
                MTextInlineFontHook.Install();
                try
                {
                    hookStatsBeforeInlineScan = MTextInlineFontHook.GetDiagnosticsSummary();
                    inlineScanResult = MTextInlineFontScanner.ScanInlineFonts(doc.Database);
                    if (inlineScanResult.InlineFonts.Count > 0)
                    {
                        // 触发型 Regen: MTextInlineFontHook 依赖 CAD 的 MText 展开/绘制流程命中实际内联字体。
                        // 这一步负责收集真实 Hook 映射结果，不改写 MText.Contents。
                        doc.Editor.Regen();
                        needsVisualRegen = false;
                    }

                    inlineFixResults = FontRuntimeMappingStore.GetInlineMappings();
                    hookStatsAfterInlineRegen = MTextInlineFontHook.GetDiagnosticsSummary();
                }
                finally
                {
                    MTextInlineFontHook.Uninstall();
                }

                contextMgr.StoreInlineFontFixResults(doc, inlineFixResults);
                DiagnosticLogger.EndPhase(
                    $"MText: {inlineScanResult.MTextCount}个, " +
                    $"内联字体: {inlineScanResult.InlineFonts.Count}个, " +
                    $"fragment展开: {inlineScanResult.FragmentExpansionSuccesses}/{inlineScanResult.FragmentExpansionAttempts}个, " +
                    $"片段: {inlineScanResult.FragmentCount}个, " +
                    $"失败: {inlineScanResult.FragmentExpansionFailures}个, " +
                    $"映射: {inlineFixResults.Count}个, " +
                    $"HookBefore=[{hookStatsBeforeInlineScan}], " +
                    $"HookAfter=[{hookStatsAfterInlineRegen}]");

                // 最终视觉刷新: 只处理永久样式写回后的显示更新。
                // 前面的两个触发型 Regen 负责 Hook 命中顺序，不在这里合并。
                if (needsVisualRegen)
                    doc.Editor.Regen();

                log.AddStatistics(
                    missingFonts,
                    stillMissing,
                    styleRuntimeMappingCount: actualStyleRuntimeMappings.Count,
                    mtextMappingCount: inlineFixResults.Count);
                DiagnosticLogger.WriteSummary();
                summarized = true;
                log.Flush();
            }

            contextMgr.MarkExecuted(doc);
        }
        catch (Exception ex)
        {
            log.Error("字体替换执行失败", ex);
        }
        finally
        {
            // 安全网: 仅在异常路径或早期返回路径时输出汇总，避免正常路径重复输出
            if (!summarized)
                DiagnosticLogger.WriteSummary();
            log.Flush();
        }
    }

    private static void ActivateStyleRuntimeFontMappings(
        IReadOnlyList<RuntimeFontMappingRecord> runtimeFontMappings)
    {
        if (runtimeFontMappings.Count == 0)
            return;

        StyleTextStyleHook.ReplaceStyleRuntimeFontMappings(runtimeFontMappings);
        DiagnosticLogger.Log("FontMapping", $"AcGiTextStyle 样式表字体 Hook 已登记: {runtimeFontMappings.Count}项");
    }

    private static void ForceLoadStyleRuntimeMappings(
        Database db,
        IReadOnlyList<RuntimeFontMappingRecord> runtimeFontMappings)
    {
        if (runtimeFontMappings.Count == 0)
            return;

        var styleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < runtimeFontMappings.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(runtimeFontMappings[i].StyleName))
                styleNames.Add(runtimeFontMappings[i].StyleName);
        }

        if (styleNames.Count == 0)
            return;

        int attempted = 0;
        int loaded = 0;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead, false, true) is not TextStyleTableRecord style)
                    continue;
                if (!styleNames.Contains(style.Name))
                    continue;

                attempted++;
                var giStyle = new Autodesk.AutoCAD.GraphicsInterface.TextStyle();
                using (StyleTextStyleHook.EnterStyleRuntimeOperation())
                {
                    giStyle.FromTextStyleTableRecord(id);
                    _ = giStyle.LoadStyleRec;
                }
                loaded++;
                DiagnosticLogger.Log("样式表运行时映射",
                    $"主动加载样式: style='{style.Name}' file='{style.FileName}' big='{style.BigFontFileName}'");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("样式表运行时映射", $"主动加载样式失败 {id}: {ex.Message}");
            }
        }

        tr.Commit();
        DiagnosticLogger.Log("样式表运行时映射", $"主动加载样式完成: attempted={attempted}, loaded={loaded}");
    }

    /// <summary>
    /// 诊断: 在 Regen 前读回样式表，验证 FontReplacer 的修改是否已写入数据库。
    /// 仅 DEBUG 构建有效 — Release 中调用点被 #if DEBUG 排除。
    /// </summary>
#if DEBUG
    private static void VerifyStyleTableAfterReplace(
        Database db, IReadOnlyList<FontCheckResult> missingFonts)
    {
        try
        {
            var missingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < missingFonts.Count; i++)
                missingNames.Add(missingFonts[i].StyleName);

            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

            foreach (ObjectId id in styleTable)
            {
                try
                {
                    var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    bool isMissing = missingNames.Contains(style.Name);
                    bool isXref = style.IsDependent;

                    // 输出被替换的样式 + 所有外参样式（排查块内乱码）
                    if (isMissing || isXref)
                    {
                        // 隔离 style.Font 访问 — 损坏的描述符不应中断诊断输出
                        string typeFace = "<读取失败>", charSet = "?", pitchFamily = "?";
                        try
                        {
                            var font = style.Font;
                            typeFace = font.TypeFace ?? string.Empty;
                            charSet = font.CharacterSet.ToString();
                            pitchFamily = font.PitchAndFamily.ToString();
                        }
                        catch { }

                        string tag = isMissing ? "[已替换]" : "[未替换]";
                        DiagnosticLogger.Log("验证", $"{tag} 样式='{style.Name}' TypeFace='{typeFace}' FileName='{style.FileName}' BigFont='{style.BigFontFileName}' CharSet={charSet} Pitch={pitchFamily}");
                    }
                }
                catch { }
            }

            tr.Commit();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("验证", $"读回样式表失败: {ex.Message}");
        }
    }
#endif
}

