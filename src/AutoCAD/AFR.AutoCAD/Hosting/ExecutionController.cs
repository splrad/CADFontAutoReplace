using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using System.Diagnostics;
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
    /// 执行阶段：检测缺失字体 → 文件级运行时映射刷新 → 样式表最终写回 → 二次验证 → 统计输出。
    /// 遵守 IsInitialized 门控（未配置则跳过）和重复执行防护（同文档只执行一次）。
    /// </para>
    /// </summary>
    /// <param name="doc">要处理的 AutoCAD 文档。</param>
    /// <param name="triggerSource">触发来源标识，用于诊断日志。</param>
    public void Execute(Document doc, string triggerSource)
    {
        if (doc == null || doc.IsDisposed)
        {
            DiagnosticLogger.Skip(
                "ExecutionController",
                "Execute",
                "目标文档为空或已释放",
                new Dictionary<string, object?> { ["trigger"] = triggerSource });
            return;
        }

        var executeTimer = Stopwatch.StartNew();
        using var dialogSystemVariables = CadDialogSystemVariableScope.Capture();
        var log = LogService.Instance;
        var config = ConfigService.Instance;
        string documentKey = DocumentContextManager.GetDocumentKey(doc) ?? "<null>";
        string documentName = DocumentContextManager.ReadDocumentName(doc);
        string databaseFilename = DocumentContextManager.ReadDatabaseFilename(doc);

        try
        {
            // 重复执行防护
            var contextMgr = DocumentContextManager.Instance;
            var executionFields = new Dictionary<string, object?>
            {
                ["trigger"] = triggerSource,
                ["documentKey"] = documentKey,
                ["documentName"] = documentName,
                ["database"] = databaseFilename
            };
            DiagnosticLogger.Start(
                "ExecutionController",
                "Execute",
                "文档字体替换执行开始",
                executionFields);
            if (contextMgr.HasExecuted(doc))
            {
                DiagnosticLogger.Skip(
                    "ExecutionController",
                    "DuplicateGuard",
                    "跳过已执行文档",
                    executionFields);
                return;
            }

            // 门控: 未配置替换字体时跳过
            if (!config.IsInitialized)
            {
                DiagnosticLogger.Skip(
                    "ExecutionController",
                    "ConfigurationGate",
                    "替换字体尚未配置，跳过文档执行",
                    executionFields);
                log.Info("请输入 AFR 命令配置替换字体。");
                return;
            }

            // 获取文档写入锁 — 修改样式表需要写锁
            DiagnosticLogger.Start("ExecutionController", "LockDocument", "开始获取文档写锁", executionFields);
            using (doc.LockDocument())
            {
                DiagnosticLogger.Ok("ExecutionController", "LockDocument", "文档写锁已获取", executionFields);
                bool needsVisualRegen = false;
                // 创建独立的字体检测上下文 — 缓存生命周期与本次执行绑定，结束后由 GC 自动回收
                var context = new FontDetectionContext(doc.Database);
                IntPtr dbScope = LdFileHook.GetDatabaseScope(doc.Database);

                DiagnosticLogger.BeginDocument(doc.Name, config.MainFont, config.BigFont, config.TrueTypeFont);
                DiagnosticLogger.Ok(
                    "ExecutionController",
                    "DocumentContext",
                    "文档执行上下文已建立",
                    executionFields);
                var runtimeStateScope = RuntimeMappingStateScope.Begin(dbScope);
                var ldFileCountersBefore = LdFileHook.GetCountersSnapshot();
                var shpLoadCountersBefore = ShpLoadHook.GetCountersSnapshot();

                int replacedStyleCount = 0;
                List<FontCheckResult> missingFonts = [];
                List<FontCheckResult> stillMissing = [];
                List<RuntimeFontMappingResultRecord> allRuntimeMappingResults = [];

                try
                {
                    // 第一阶段: 检测缺失字体（读取样式表原始状态，判断哪些字体在系统中不可用）。
                    // 此阶段不做永久写回，保证运行时映射先处理原始图纸状态。
                    var detectTimer = Stopwatch.StartNew();
                    DiagnosticLogger.Start("ExecutionController", "DetectMissingFonts", "检测缺失字体开始", executionFields);
                    missingFonts = FontDetector.DetectMissingFonts(context);

                    // 存储检测结果供 AFRLOG 命令使用
                    contextMgr.StoreDetectionResults(doc, missingFonts);
                    detectTimer.Stop();
                    DiagnosticLogger.Ok(
                        "ExecutionController",
                        "DetectMissingFonts",
                        "检测缺失字体完成",
                        new Dictionary<string, object?>
                        {
                            ["trigger"] = triggerSource,
                            ["documentKey"] = documentKey,
                            ["missingFonts"] = missingFonts.Count
                        },
                        detectTimer.ElapsedMilliseconds);

                    // 第二阶段: 样式表写回前先触发图形刷新，让文件级 Hook 看到原始字体加载请求。
                    var runtimeMappingTimer = Stopwatch.StartNew();
                    DiagnosticLogger.Start(
                        "ExecutionController",
                        "RuntimeMappingRegen",
                        "样式表写回前触发文件级运行时映射刷新开始",
                        new Dictionary<string, object?>
                        {
                            ["missingFonts"] = missingFonts.Count,
                            ["ldFileInstalled"] = LdFileHook.IsInstalled,
                            ["shpLoadInstalled"] = ShpLoadHook.IsInstalled
                        });
                    doc.Editor.Regen();
                    runtimeMappingTimer.Stop();
                    DiagnosticLogger.Ok(
                        "ExecutionController",
                        "RuntimeMappingRegen",
                        "样式表写回前触发文件级运行时映射刷新完成",
                        new Dictionary<string, object?>
                        {
                            ["missingFonts"] = missingFonts.Count,
                            ["ldFileInstalled"] = LdFileHook.IsInstalled,
                            ["shpLoadInstalled"] = ShpLoadHook.IsInstalled
                        },
                        runtimeMappingTimer.ElapsedMilliseconds);

                    allRuntimeMappingResults = FontRuntimeMappingStore.GetRuntimeMappingResults();
                    contextMgr.StoreRuntimeFontMappingResults(doc, allRuntimeMappingResults);
                    DiagnosticLogger.Ok(
                        "ExecutionController",
                        "CollectRuntimeMappingResults",
                        "文件级运行时映射结果已采集",
                        new Dictionary<string, object?>
                        {
                            ["runtimeMappingHits"] = allRuntimeMappingResults.Count,
                            ["ldFileRedirects"] = LdFileHook.GetCountersSnapshot().RedirectCount - ldFileCountersBefore.RedirectCount,
                            ["shpLoadRedirects"] = ShpLoadHook.GetCountersSnapshot().RedirectCount - shpLoadCountersBefore.RedirectCount
                        });

                    // 第三阶段: 样式表最终写回。FontDetector 仅排除 @TrueType，
                    // SHX 缺失字体（包含 @SHX）在这里永久写回样式表。
                    var finalWriteTimer = Stopwatch.StartNew();
                    DiagnosticLogger.Start(
                        "ExecutionController",
                        "ReplaceMissingFonts",
                        "样式表最终写回开始",
                        new Dictionary<string, object?> { ["missingFonts"] = missingFonts.Count });
                    if (missingFonts.Count > 0)
                    {
                        replacedStyleCount = FontReplacer.ReplaceMissingFonts(
                            missingFonts,
                            config.MainFont,
                            config.BigFont,
                            config.TrueTypeFont,
                            context);

                        if (replacedStyleCount > 0)
                        {
                            needsVisualRegen = true;
                        }
                    }
                    else
                    {
                        DiagnosticLogger.Skip(
                            "ExecutionController",
                            "ReplaceMissingFonts",
                            "没有普通缺失字体需要样式表最终写回",
                            new Dictionary<string, object?> { ["missingFonts"] = 0 });
                    }

                    finalWriteTimer.Stop();
                    DiagnosticLogger.Ok(
                        "ExecutionController",
                        "ReplaceMissingFonts",
                        "样式表最终写回完成",
                        new Dictionary<string, object?>
                        {
                            ["missingFonts"] = missingFonts.Count,
                            ["replacedStyleCount"] = replacedStyleCount
                        },
                        finalWriteTimer.ElapsedMilliseconds);

                    // 第四阶段: 二次检测替换后的样式表状态，供 AFRLOG 标记仍缺失样式。
                    var postDetectTimer = Stopwatch.StartNew();
                    DiagnosticLogger.Start("ExecutionController", "PostDetectMissingFonts", "替换后二次检测开始");
                    var postContext = new FontDetectionContext(doc.Database);
                    stillMissing = FontDetector.DetectMissingFonts(postContext);
                    contextMgr.StoreStillMissingResults(doc, stillMissing);
                    postDetectTimer.Stop();
                    DiagnosticLogger.Ok(
                        "ExecutionController",
                        "PostDetectMissingFonts",
                        "替换后二次检测完成",
                        new Dictionary<string, object?> { ["stillMissing"] = stillMissing.Count },
                        postDetectTimer.ElapsedMilliseconds);
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
                    DiagnosticLogger.Start(
                        "ExecutionController",
                        "FinalVisualRegen",
                        "样式表最终写回后执行文档级登记已清理的最终 Regen",
                        new Dictionary<string, object?> { ["markedGraphics"] = markedGraphics });
                    doc.Editor.Regen();
                    DiagnosticLogger.Ok(
                        "ExecutionController",
                        "FinalVisualRegen",
                        "最终视觉刷新完成",
                        new Dictionary<string, object?> { ["markedGraphics"] = markedGraphics });
                }
                else
                {
                    DiagnosticLogger.Skip(
                        "ExecutionController",
                        "FinalVisualRegen",
                        "没有样式表最终写回，不执行最终视觉刷新");
                }

                var ldFileCountersAfter = LdFileHook.GetCountersSnapshot();
                DiagnosticLogger.Ok(
                    "LdFileHook",
                    "DocumentCounters",
                    "本次文档 ldfile 计数已采集",
                    new Dictionary<string, object?>
                    {
                        ["hits"] = ldFileCountersAfter.HitCount - ldFileCountersBefore.HitCount,
                        ["redirects"] = ldFileCountersAfter.RedirectCount - ldFileCountersBefore.RedirectCount,
                        ["sessionHits"] = ldFileCountersAfter.HitCount,
                        ["sessionRedirects"] = ldFileCountersAfter.RedirectCount
                    });
                ShpLoadHook.LogDocumentSummary(shpLoadCountersBefore);

                log.AddStatistics(
                    missingFonts,
                    stillMissing,
                    runtimeMappingCount: allRuntimeMappingResults.Count);
                DiagnosticLogger.Ok(
                    "ExecutionController",
                    "ExecutionStatistics",
                    "文档执行统计已写入用户日志",
                    new Dictionary<string, object?>
                    {
                        ["missingFonts"] = missingFonts.Count,
                        ["stillMissing"] = stillMissing.Count,
                        ["replacedStyleCount"] = replacedStyleCount,
                        ["runtimeMappingCount"] = allRuntimeMappingResults.Count
                    });
                log.Flush();
            }

            contextMgr.MarkExecuted(doc);
            DiagnosticLogger.Ok(
                "ExecutionController",
                "MarkExecuted",
                "文档执行状态已标记",
                new Dictionary<string, object?>
                {
                    ["documentKey"] = documentKey,
                    ["documentName"] = documentName
                });
            executeTimer.Stop();
            DiagnosticLogger.Ok(
                "ExecutionController",
                "Execute",
                "文档字体替换执行完成",
                new Dictionary<string, object?>
                {
                    ["trigger"] = triggerSource,
                    ["documentKey"] = documentKey,
                    ["documentName"] = documentName,
                    ["database"] = databaseFilename
                },
                executeTimer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            executeTimer.Stop();
            DiagnosticLogger.Fail(
                "ExecutionController",
                "Execute",
                "字体替换执行失败",
                ex,
                new Dictionary<string, object?>
                {
                    ["trigger"] = triggerSource,
                    ["documentKey"] = documentKey,
                    ["documentName"] = documentName,
                    ["database"] = databaseFilename
                },
                executeTimer.ElapsedMilliseconds);
            log.Error("字体替换执行失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }

    private sealed class RuntimeMappingStateScope : IDisposable
    {
        private bool _disposed;

        private RuntimeMappingStateScope()
        {
        }

        internal static RuntimeMappingStateScope Begin(IntPtr dbScope)
        {
            DiagnosticLogger.Start(
                "RuntimeMappingStateScope",
                "Begin",
                "文档级运行时映射状态清理开始",
                new Dictionary<string, object?> { ["dbScope"] = FormatDbScope(dbScope) });
            ClearDocumentRuntimeState(dbScope, clearDocumentFileMappings: true, clearTransientFileMappings: false);
            DiagnosticLogger.Ok(
                "RuntimeMappingStateScope",
                "Begin",
                "文档级运行时映射状态已清理",
                new Dictionary<string, object?> { ["dbScope"] = FormatDbScope(dbScope) });
            return new RuntimeMappingStateScope();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DiagnosticLogger.Start(
                "RuntimeMappingStateScope",
                "Dispose",
                "文档级运行时映射状态结束清理开始");
            ClearDocumentRuntimeState(
                IntPtr.Zero,
                clearDocumentFileMappings: false,
                clearTransientFileMappings: true);
            DiagnosticLogger.Ok(
                "RuntimeMappingStateScope",
                "Dispose",
                "文档级运行时映射状态结束清理完成");
        }

        private static void ClearDocumentRuntimeState(
            IntPtr dbScope,
            bool clearDocumentFileMappings,
            bool clearTransientFileMappings)
        {
            FontRuntimeMappingStore.Clear();
            if (clearDocumentFileMappings)
                LdFileHook.ClearRegisteredRedirectsForDocument(dbScope);
            if (clearTransientFileMappings)
                LdFileHook.ClearTransientRegisteredRedirects();
        }

        private static string FormatDbScope(IntPtr dbScope)
            => dbScope == IntPtr.Zero ? "0x0" : $"0x{dbScope.ToInt64():X}";
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
            DiagnosticLogger.Skip(
                "ExecutionController",
                "MarkAffectedTextGraphicsModified",
                "未找到需刷新文字样式",
                new Dictionary<string, object?> { ["affectedStyleNames"] = affectedStyleNames.Count });
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
        DiagnosticLogger.Ok(
            "ExecutionController",
            "MarkAffectedTextGraphicsModified",
            "已标记文字图形缓存",
            new Dictionary<string, object?>
            {
                ["styles"] = affectedStyleIds.Count,
                ["dirtyBlocks"] = dirtyBlockDefinitions.Count,
                ["text"] = textMarked,
                ["attributes"] = attributeMarked,
                ["blockRefs"] = blockReferenceMarked,
                ["errors"] = errors
            });

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
                        DiagnosticLogger.Ok(
                            "ExecutionController",
                            "VerifyStyleTableReadBack",
                            "样式表读回验证记录",
                            new Dictionary<string, object?>
                            {
                                ["tag"] = tag,
                                ["styleName"] = style.Name,
                                ["typeFace"] = typeFace,
                                ["fileName"] = style.FileName,
                                ["bigFontFileName"] = style.BigFontFileName,
                                ["characterSet"] = charSet,
                                ["pitchAndFamily"] = pitchFamily
                            });
                    }
                }
                catch { }
            }

            tr.Commit();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Fail(
                "ExecutionController",
                "VerifyStyleTableReadBack",
                "读回样式表失败",
                ex);
        }
    }
#endif
}

