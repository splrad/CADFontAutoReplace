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
    /// 执行阶段：检测缺失字体 → 样式表运行时映射 → MText 内联运行时映射 → 样式表最终写回 → 二次验证 → 统计输出。
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
            string documentKey = DocumentContextManager.GetDocumentKey(doc) ?? "<null>";
            string documentName = DocumentContextManager.ReadDocumentName(doc);
            string databaseFilename = DocumentContextManager.ReadDatabaseFilename(doc);
            if (contextMgr.HasExecuted(doc))
            {
                DiagnosticLogger.Log(
                    "文档",
                    $"跳过已执行文档: trigger='{triggerSource}' key='{documentKey}' name='{documentName}' database='{databaseFilename}'");
                return;
            }

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

                DiagnosticLogger.BeginDocument(doc.Name, config.MainFont, config.BigFont, config.TrueTypeFont);
                DiagnosticLogger.Log(
                    "文档",
                    $"执行触发: trigger='{triggerSource}' key='{documentKey}' name='{documentName}' database='{databaseFilename}'");
                var runtimeStateScope = SourceFontRuntimeStateScope.Begin();
                var ldFileCountersBefore = LdFileHook.GetCountersSnapshot();
                var shpLoadCountersBefore = ShpLoadHook.GetCountersSnapshot();
#if DEBUG
                var mapFontCountersBefore = MapFontDiagnosticHook.GetCountersSnapshot();
#endif

                int replacedStyleCount = 0;
                List<FontCheckResult> missingFonts = [];
                List<FontCheckResult> stillMissing = [];
                List<RuntimeFontMappingResultRecord> actualStyleRuntimeMappings = [];
                List<RuntimeFontMappingResultRecord> allRuntimeMappingResults = [];

                try
                {
                    // 第一阶段: 检测缺失字体（读取样式表原始状态，判断哪些字体在系统中不可用）。
                    // 此阶段不做永久写回，保证运行时映射先处理原始图纸状态。
                    DiagnosticLogger.BeginPhase("检测缺失字体");
                    missingFonts = FontDetector.DetectMissingFonts(context);

                    // 存储检测结果供 AFRLOG 命令使用
                    contextMgr.StoreDetectionResults(doc, missingFonts);
                    DiagnosticLogger.EndPhase($"缺失: {missingFonts.Count}个");

                    // 第二阶段: 仅登记样式表 @TrueType 缺失字体的临时运行时映射（不立即 Regen）。
                    // 与第三阶段的 MText 内联登记合并后，用一次触发型 Regen 统一命中两类 Hook，
                    // 避免连续多次 Regen 触发 NVIDIA GPU 驱动（nvgpucomp64.dll）渲染管线竞态。
                    var runtimeFontMappings = FontDetector.CollectRuntimeFontMappings(context, config.TrueTypeFont);
                    bool hasStyleRuntimeMappings = runtimeFontMappings.Count > 0;
                    if (hasStyleRuntimeMappings)
                    {
                        DiagnosticLogger.BeginPhase("样式表运行时映射登记");
                        ActivateStyleRuntimeFontMappings(runtimeFontMappings);
                        ForceLoadStyleRuntimeMappings(doc.Database, runtimeFontMappings);
                        DiagnosticLogger.EndPhase($"登记: {runtimeFontMappings.Count}项（等待合并触发型 Regen）");
                    }

                    // 第三阶段: 只读扫描 MText 内联字体；实际映射结果由文件级 Hook 记录。
                    // 不改写 MText.Contents，避免接管 CAD 原生内联格式解析。
                    DiagnosticLogger.BeginPhase("扫描MText内联字体");
                    MTextInlineFontScanResult inlineScanResult;
                    string hookStatsBeforeInlineScan = MTextInlineFontHook.GetDiagnosticsSummary();
                    string hookStatsAfterInlineRegen = hookStatsBeforeInlineScan;
                    int preRegisteredInlineMappings = 0;
                    string runtimeBridgeRegistrationSummary;
                    DiagnosticLogger.Log("MTextInlineFontScanner", "开始只读扫描 MText.Contents。");
                    inlineScanResult = MTextInlineFontScanner.ScanInlineFonts(doc.Database);
                    string inlineCandidateSummary = BuildInlineCandidateSummary(inlineScanResult.InlineFonts);
                    DiagnosticLogger.Log("MTextInlineFontScanner",
                        $"只读扫描完成: MText={inlineScanResult.MTextCount}, 内联字体候选={inlineScanResult.InlineFonts.Count}, {inlineCandidateSummary}");

                    if (inlineScanResult.InlineFonts.Count > 0)
                    {
                        hookStatsBeforeInlineScan = MTextInlineFontHook.GetDiagnosticsSummary();
                        DiagnosticLogger.Log("MTextInlineFontHook",
                            $"MText预登记开始: 候选={inlineScanResult.InlineFonts.Count}, {inlineCandidateSummary}");
                        preRegisteredInlineMappings = MTextInlineFontHook.PreRegisterRuntimeRequests();
                        DiagnosticLogger.Log("MTextInlineFontHook",
                            $"MText预登记结束: 登记={preRegisteredInlineMappings}, 候选={inlineScanResult.InlineFonts.Count}, {inlineCandidateSummary}");
                    }
                    else
                    {
                        DiagnosticLogger.Log("MTextInlineFontHook", "MText无内联字体候选，跳过预登记。");
                    }

                    // 合并触发型 Regen：样式表运行时映射（StyleTextStyleHook）和
                    // MText 内联运行时映射（MTextInlineFontHook）共用一次 Regen，
                    // 将单文档最大 Regen 次数从 3 次降为 2 次，避免 GPU 驱动堆损坏。
                    bool needsCombinedRegen = hasStyleRuntimeMappings || preRegisteredInlineMappings > 0;
                    if (needsCombinedRegen)
                    {
                        DiagnosticLogger.Log("运行时映射", $"合并触发型 Regen 开始（样式表={hasStyleRuntimeMappings}, MText登记={preRegisteredInlineMappings}）。");
                        doc.Editor.Regen();
                        DiagnosticLogger.Log("运行时映射", "合并触发型 Regen 结束。");
                        hookStatsAfterInlineRegen = MTextInlineFontHook.GetDiagnosticsSummary();
                    }
                    else
                    {
                        DiagnosticLogger.Log("运行时映射", "无样式表运行时映射且无MText文件级登记，跳过触发型 Regen。");
                    }

                    if (hasStyleRuntimeMappings)
                    {
                        actualStyleRuntimeMappings = FontRuntimeMappingStore.GetRuntimeMappingResults()
                            .Where(item => string.Equals(item.Source, "样式表", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        contextMgr.StoreRuntimeFontMappingResults(doc, actualStyleRuntimeMappings);
                        DiagnosticLogger.Log("样式表运行时映射", $"命中: {actualStyleRuntimeMappings.Count}项");
                    }
                    else
                    {
                        contextMgr.StoreRuntimeFontMappingResults(doc, actualStyleRuntimeMappings);
                    }

                    allRuntimeMappingResults = FontRuntimeMappingStore.GetRuntimeMappingResults();
                    runtimeBridgeRegistrationSummary = BuildInlineRuntimeBridgeRegistrationSummary(
                        FontRuntimeRequestRegistry.GetDiagnosticsSnapshot());
                    DiagnosticLogger.Log("LdFileHook",
                        $"MText内联字体加载桥接登记诊断: {runtimeBridgeRegistrationSummary}");

                    contextMgr.StoreInlineFontFixResults(doc, []);
                    // 用包含所有来源（样式表 + MText）的全量结果覆盖写入，
                    // 前面各阶段的分阶段写入在此被最终结果替换。
                    contextMgr.StoreRuntimeFontMappingResults(doc, allRuntimeMappingResults);
                    DiagnosticLogger.EndPhase(
                        $"MText: {inlineScanResult.MTextCount}个, " +
                        $"内联字体: {inlineScanResult.InlineFonts.Count}个, " +
                        $"fragment展开: {inlineScanResult.FragmentExpansionSuccesses}/{inlineScanResult.FragmentExpansionAttempts}个, " +
                        $"片段: {inlineScanResult.FragmentCount}个, " +
                        $"失败: {inlineScanResult.FragmentExpansionFailures}个, " +
                        $"映射: {allRuntimeMappingResults.Count(item => string.Equals(item.Source, "MText", StringComparison.OrdinalIgnoreCase))}个, " +
                        $"Regen前内联登记: {preRegisteredInlineMappings}个, " +
                        $"字体加载桥接登记=[{runtimeBridgeRegistrationSummary}], " +
                        $"HookBefore=[{hookStatsBeforeInlineScan}], " +
                        $"HookAfter=[{hookStatsAfterInlineRegen}]");

                    // 第四阶段: 样式表最终写回。FontDetector 仅排除 @TrueType，
                    // SHX 缺失字体（包含 @SHX）在这里永久写回样式表。
                    DiagnosticLogger.BeginPhase("样式表最终写回阶段");
                    if (missingFonts.Count > 0)
                    {
                        using (MTextInlineFontHook.SuppressInlineRuntimeMapping())
                        {
                            replacedStyleCount = FontReplacer.ReplaceMissingFonts(
                                missingFonts,
                                config.MainFont,
                                config.BigFont,
                                config.TrueTypeFont,
                                context);
                        }

                        if (replacedStyleCount > 0)
                        {
                            needsVisualRegen = true;
                        }
                    }

                    DiagnosticLogger.EndPhase($"最终写回替换: {replacedStyleCount}项");

                    // 第五阶段: 二次检测替换后的样式表状态，供 AFRLOG 标记仍缺失样式。
                    DiagnosticLogger.BeginPhase("替换后二次检测");
                    var postContext = new FontDetectionContext(doc.Database);
                    stillMissing = FontDetector.DetectMissingFonts(postContext);
                    contextMgr.StoreStillMissingResults(doc, stillMissing);
                    DiagnosticLogger.EndPhase($"仍缺失: {stillMissing.Count}个");
                }
                finally
                {
                    runtimeStateScope.Dispose();
                }

                // 最终视觉刷新: 只处理永久样式写回后的显示更新。
                // 前面的两个触发型 Regen 负责 Hook 命中顺序，不在这里合并。
                if (needsVisualRegen)
                {
                    int markedGraphics = MarkAffectedTextGraphicsModified(doc.Database, missingFonts);
                    DiagnosticLogger.Log(
                        "图形刷新",
                        $"样式表最终写回后执行文档级登记已清理的最终Regen: marked={markedGraphics}");
                    doc.Editor.Regen();
                }

                var ldFileCountersAfter = LdFileHook.GetCountersSnapshot();
                DiagnosticLogger.Log(
                    "LdFileHook",
                    $"本次文档 ldfile 计数: hits={ldFileCountersAfter.HitCount - ldFileCountersBefore.HitCount}, " +
                    $"redirects={ldFileCountersAfter.RedirectCount - ldFileCountersBefore.RedirectCount}, " +
                    $"sessionHits={ldFileCountersAfter.HitCount}, sessionRedirects={ldFileCountersAfter.RedirectCount}");
#if DEBUG
                MapFontDiagnosticHook.LogDocumentSummary(mapFontCountersBefore);
#endif
                ShpLoadHook.LogDocumentSummary(shpLoadCountersBefore);

                log.AddStatistics(
                    missingFonts,
                    stillMissing,
                    styleRuntimeMappingCount: actualStyleRuntimeMappings.Count,
                    mtextMappingCount: allRuntimeMappingResults.Count(item => string.Equals(item.Source, "MText", StringComparison.OrdinalIgnoreCase)));
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

    private static string BuildInlineRuntimeBridgeRegistrationSummary(
        IReadOnlyList<FontRuntimeRequestDiagnostic> diagnostics)
    {
        var inlineDiagnostics = diagnostics
            .Where(item => item.InlineType.HasValue)
            .ToArray();
        if (inlineDiagnostics.Length == 0)
            return "登记=0, 命中=0, 未命中=0";

        int hitCount = inlineDiagnostics.Count(item => item.Hit);
        int missedCount = inlineDiagnostics.Length - hitCount;
        if (missedCount == 0)
            return $"登记={inlineDiagnostics.Length}, 命中={hitCount}, 未命中=0";

        var missed = inlineDiagnostics
            .Where(item => !item.Hit)
            .Take(8)
            .Select(item =>
                $"{item.InlineType}:{item.OriginalDisplayFont}->{item.ReplacementFont} source={item.Source}");
        string missedText = string.Join("; ", missed);
        if (missedCount > 8)
            missedText += $"; ...+{missedCount - 8}";

        return $"登记={inlineDiagnostics.Length}, 命中={hitCount}, 未命中={missedCount}, 未命中项=[{missedText}]";
    }

    private static string BuildInlineCandidateSummary(
        IReadOnlyDictionary<string, InlineFontCandidate> inlineFonts)
    {
        int trueTypeCount = 0;
        int shxMainCount = 0;
        int shxBigFontCount = 0;

        foreach (var candidate in inlineFonts.Values)
        {
            switch (candidate.FontType)
            {
                case InlineFontType.TrueType:
                    trueTypeCount++;
                    break;
                case InlineFontType.ShxMain:
                    shxMainCount++;
                    break;
                case InlineFontType.ShxBigFont:
                    shxBigFontCount++;
                    break;
            }
        }

        return $"TrueType={trueTypeCount}, SHX主字体={shxMainCount}, SHX大字体={shxBigFontCount}";
    }

    private sealed class SourceFontRuntimeStateScope : IDisposable
    {
        private bool _disposed;

        private SourceFontRuntimeStateScope()
        {
        }

        internal static SourceFontRuntimeStateScope Begin()
        {
            DiagnosticLogger.Log("来源状态", "文档级来源状态开始，清理运行时登记、样式映射、MText候选和诊断计数。");
            ClearDocumentRuntimeState();
            return new SourceFontRuntimeStateScope();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DiagnosticLogger.Log("来源状态", "文档级来源状态结束，清理运行时登记、样式映射和MText候选。");
            ClearDocumentRuntimeState();
            DiagnosticLogger.Log(
                "来源状态",
                $"文档级来源状态已清理: styleHook={StyleTextStyleHook.IsInstalled}, mtextHook={MTextInlineFontHook.IsInstalled}");
        }

        private static void ClearDocumentRuntimeState()
        {
            FontRuntimeMappingStore.Clear();
            LdFileHook.ClearRegisteredRedirects();
            StyleTextStyleHook.ReplaceStyleRuntimeFontMappings(Array.Empty<RuntimeFontMappingRecord>());
            MTextInlineFontHook.ClearInlineFontCandidates();
            MTextInlineFontHook.ResetDiagnosticsCounters();
        }
    }

    private static int MarkAffectedTextGraphicsModified(
        Database db,
        IReadOnlyList<FontCheckResult> missingFonts)
    {
        if (missingFonts.Count == 0)
            return 0;

        var affectedStyleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < missingFonts.Count; i++)
        {
            FontCheckResult missing = missingFonts[i];
            if ((missing.IsMainFontMissing || missing.IsBigFontMissing)
                && !string.IsNullOrWhiteSpace(missing.StyleName))
            {
                affectedStyleNames.Add(missing.StyleName);
            }
        }

        if (affectedStyleNames.Count == 0)
            return 0;

        int textMarked = 0;
        int attributeMarked = 0;
        int blockReferenceMarked = 0;
        int errors = 0;

        using var tr = db.TransactionManager.StartTransaction();
        var affectedStyleIds = ResolveAffectedStyleIds(db, tr, affectedStyleNames);
        if (affectedStyleIds.Count == 0)
        {
            tr.Commit();
            DiagnosticLogger.Log(
                "图形刷新",
                $"未找到需刷新文字样式: names={affectedStyleNames.Count}");
            return 0;
        }

        var dirtyBlockDefinitions = CollectDirtyBlockDefinitions(db, tr, affectedStyleIds);
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId btrId in bt)
        {
            try
            {
                if (tr.GetObject(btrId, OpenMode.ForRead, false, true) is not BlockTableRecord btr)
                    continue;

                foreach (ObjectId entId in btr)
                {
                    try
                    {
                        if (tr.GetObject(entId, OpenMode.ForRead, false, true) is not Entity entity)
                            continue;

                        if (UsesAffectedTextStyle(entity, affectedStyleIds)
                            && TryMarkGraphicsModified(entity))
                        {
                            textMarked++;
                        }

                        if (entity is BlockReference blockReference)
                        {
                            int markedAttributes = MarkAffectedAttributes(blockReference, tr, affectedStyleIds);
                            attributeMarked += markedAttributes;

                            bool referencesDirtyDefinition =
                                dirtyBlockDefinitions.Contains(blockReference.BlockTableRecord);
                            if ((markedAttributes > 0 || referencesDirtyDefinition)
                                && TryMarkGraphicsModified(blockReference))
                            {
                                blockReferenceMarked++;
                            }
                        }
                    }
                    catch
                    {
                        errors++;
                    }
                }
            }
            catch
            {
                errors++;
            }
        }

        tr.Commit();
        DiagnosticLogger.Log(
            "图形刷新",
            $"已标记文字图形缓存: styles={affectedStyleIds.Count}, dirtyBlocks={dirtyBlockDefinitions.Count}, " +
            $"text={textMarked}, attributes={attributeMarked}, blockRefs={blockReferenceMarked}, errors={errors}");

        return textMarked + attributeMarked + blockReferenceMarked;
    }

    private static HashSet<ObjectId> ResolveAffectedStyleIds(
        Database db,
        Transaction tr,
        HashSet<string> affectedStyleNames)
    {
        var styleIds = new HashSet<ObjectId>();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead, false, true) is TextStyleTableRecord style
                    && affectedStyleNames.Contains(style.Name))
                {
                    styleIds.Add(id);
                }
            }
            catch
            {
                // 跳过无法读取的样式。
            }
        }

        return styleIds;
    }

    private static HashSet<ObjectId> CollectDirtyBlockDefinitions(
        Database db,
        Transaction tr,
        HashSet<ObjectId> affectedStyleIds)
    {
        var dirtyBlocks = new HashSet<ObjectId>();
        var referencesByOwner = new Dictionary<ObjectId, List<ObjectId>>();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId btrId in bt)
        {
            try
            {
                if (tr.GetObject(btrId, OpenMode.ForRead, false, true) is not BlockTableRecord btr)
                    continue;

                foreach (ObjectId entId in btr)
                {
                    try
                    {
                        if (tr.GetObject(entId, OpenMode.ForRead, false, true) is not Entity entity)
                            continue;

                        if (UsesAffectedTextStyle(entity, affectedStyleIds))
                            dirtyBlocks.Add(btrId);

                        if (entity is BlockReference blockReference
                            && !blockReference.BlockTableRecord.IsNull)
                        {
                            if (!referencesByOwner.TryGetValue(btrId, out var references))
                            {
                                references = [];
                                referencesByOwner[btrId] = references;
                            }

                            references.Add(blockReference.BlockTableRecord);
                        }
                    }
                    catch
                    {
                        // 跳过无法读取的实体。
                    }
                }
            }
            catch
            {
                // 跳过无法读取的块定义。
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var pair in referencesByOwner)
            {
                if (dirtyBlocks.Contains(pair.Key))
                    continue;

                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (!dirtyBlocks.Contains(pair.Value[i]))
                        continue;

                    dirtyBlocks.Add(pair.Key);
                    changed = true;
                    break;
                }
            }
        }
        while (changed);

        return dirtyBlocks;
    }

    private static int MarkAffectedAttributes(
        BlockReference blockReference,
        Transaction tr,
        HashSet<ObjectId> affectedStyleIds)
    {
        int marked = 0;
        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            try
            {
                if (tr.GetObject(attributeId, OpenMode.ForRead, false, true) is AttributeReference attribute
                    && affectedStyleIds.Contains(attribute.TextStyleId)
                    && TryMarkGraphicsModified(attribute))
                {
                    marked++;
                }
            }
            catch
            {
                // 跳过无法访问的块属性。
            }
        }

        return marked;
    }

    private static bool UsesAffectedTextStyle(Entity entity, HashSet<ObjectId> affectedStyleIds)
    {
        return entity switch
        {
            DBText dbText => affectedStyleIds.Contains(dbText.TextStyleId),
            MText mText => affectedStyleIds.Contains(mText.TextStyleId),
            _ => false
        };
    }

    private static bool TryMarkGraphicsModified(Entity entity)
    {
        try
        {
            if (!entity.IsWriteEnabled)
                entity.UpgradeOpen();
            entity.RecordGraphicsModified(true);
            return true;
        }
        catch
        {
            return false;
        }
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

