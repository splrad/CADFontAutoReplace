using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using System.Diagnostics;
using AFR.FontMapping;
using AFR.Models;
using AFR.Services;

namespace AFR.Hosting;

/// <summary>
/// 文档级字体处理控制器。
/// <para>
/// 负责缺失字体检测、样式表写回、Regen 触发运行时 Hook，以及结果采集。
/// </para>
/// </summary>
internal static class ExecutionController
{
    /// <summary>
    /// 执行一次文档字体处理。
    /// <para>
    /// 顺序：检测缺失字体 -> 样式表写回 -> 二次检测 -> Regen 触发文件级 Hook -> 采集真实 Hook 命中结果。
    /// 未配置替换字体或同一文档已处理时会直接跳过。
    /// </para>
    /// </summary>
    /// <param name="doc">要处理的 AutoCAD 文档。</param>
    /// <param name="triggerSource">触发来源标识，用于诊断日志。</param>
    public static void Execute(Document doc, string triggerSource)
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
        string diagnosticDocumentName = GetDiagnosticDocumentName(documentKey, documentName, databaseFilename);

        try
        {
            var contextMgr = DocumentContextManager.Instance;
            var executionFields = new Dictionary<string, object?>
            {
                ["doc"] = diagnosticDocumentName,
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

            // 后续可能写样式表，必须持有文档写锁。
            DiagnosticLogger.Start("ExecutionController", "LockDocument", "开始获取文档写锁", executionFields);
            using (doc.LockDocument())
            {
                DiagnosticLogger.Ok("ExecutionController", "LockDocument", "文档写锁已获取", executionFields);
                bool needsVisualRegen = false;
                // 检测缓存只属于本次文档执行，避免跨图纸污染。
                var context = new FontDetectionContext(doc.Database);
                IntPtr dbScope = LdFileHook.GetDatabaseScope(doc.Database);

                DiagnosticLogger.BeginDocument(doc.Name, config.MainFont, config.BigFont, config.TrueTypeFont);
                DiagnosticLogger.Ok(
                    "ExecutionController",
                    "DocumentContext",
                    "文档执行上下文已建立",
                    executionFields);
                var runtimeStateScope = RuntimeMappingStateScope.Begin(dbScope);
                (long ldFileHitCountBefore, long ldFileRedirectCountBefore) = LdFileHook.GetCountersSnapshot();
                var shpLoadCountersBefore = ShpLoadHook.GetCountersSnapshot();

                int replacedStyleCount = 0;
                List<FontCheckResult> missingFonts = [];
                List<FontCheckResult> stillMissing = [];
                List<RuntimeFontMappingResultRecord> allRuntimeMappingResults = [];

                try
                {
                    // 读取样式表原始状态；此处只检测，不写回。
                    var detectTimer = Stopwatch.StartNew();
                    DiagnosticLogger.Start("ExecutionController", "DetectMissingFonts", "检测缺失字体开始", executionFields);
                    missingFonts = FontDetector.DetectMissingFonts(context);

                    // AFRLOG 需要原始缺失结果来显示已替换过的样式。
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

                    // 永久修复样式表缺失字体；运行时 Hook 只处理后续 Regen 中出现的文件加载。
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
                            "没有缺失字体需要样式表最终写回",
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

                    // 写回后再检测一次，用于标记仍缺失的样式。
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

                    // Regen 放在样式表写回之后：既刷新显示，也让文件级 Hook 捕获运行时字体加载。
                    int markedGraphics = 0;
                    if (needsVisualRegen)
                    {
                        markedGraphics = MarkAffectedTextGraphicsModified(doc.Database, missingFonts);
                    }
                    else
                    {
                        DiagnosticLogger.Skip(
                            "ExecutionController",
                            "MarkAffectedTextGraphicsModified",
                            "没有样式表最终写回，不标记图形缓存");
                    }

                    var inlineRuntimeTimer = Stopwatch.StartNew();
                    DiagnosticLogger.Start(
                        "ExecutionController",
                        "InlineRuntimeMappingRegen",
                        "样式表回写后触发内联字体运行时映射刷新开始",
                        new Dictionary<string, object?>
                        {
                            ["markedGraphics"] = markedGraphics,
                            ["styleWriteback"] = needsVisualRegen,
                            ["ldFileInstalled"] = LdFileHook.IsInstalled,
                            ["shpLoadInstalled"] = ShpLoadHook.IsInstalled
                        });
                    doc.Editor.Regen();
                    inlineRuntimeTimer.Stop();
                    DiagnosticLogger.Ok(
                        "ExecutionController",
                        "InlineRuntimeMappingRegen",
                        "样式表回写后内联字体运行时映射刷新完成",
                        new Dictionary<string, object?>
                        {
                            ["markedGraphics"] = markedGraphics,
                            ["styleWriteback"] = needsVisualRegen,
                            ["ldFileInstalled"] = LdFileHook.IsInstalled,
                            ["shpLoadInstalled"] = ShpLoadHook.IsInstalled
                        },
                        inlineRuntimeTimer.ElapsedMilliseconds);

                    allRuntimeMappingResults = FontRuntimeMappingStore.GetRuntimeMappingResults();
                    contextMgr.StoreRuntimeFontMappingResults(doc, allRuntimeMappingResults);
                    DiagnosticLogger.Ok(
                        "ExecutionController",
                        "CollectRuntimeMappingResults",
                        "内联字体运行时映射结果已采集",
                        new Dictionary<string, object?>
                        {
                            ["counterWindow"] = "ExecutionController",
                            ["runtimeMappingHits"] = allRuntimeMappingResults.Count,
                            ["ldFileRedirects"] = LdFileHook.GetCountersSnapshot().RedirectCount - ldFileRedirectCountBefore,
                            ["shpLoadRedirects"] = ShpLoadHook.GetCountersSnapshot().RedirectCount - shpLoadCountersBefore.RedirectCount
                        });
                }
                finally
                {
                    runtimeStateScope.Dispose();
                }

                (long ldFileHitCountAfter, long ldFileRedirectCountAfter) = LdFileHook.GetCountersSnapshot();
                DiagnosticLogger.Ok(
                    "LdFileHook",
                    "DocumentCounters",
                    "本次文档 ldfile 计数已采集",
                    new Dictionary<string, object?>
                    {
                        ["counterWindow"] = "ExecutionController",
                        ["hits"] = ldFileHitCountAfter - ldFileHitCountBefore,
                        ["redirects"] = ldFileRedirectCountAfter - ldFileRedirectCountBefore,
                        ["sessionHits"] = ldFileHitCountAfter,
                        ["sessionRedirects"] = ldFileRedirectCountAfter
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
                    ["doc"] = diagnosticDocumentName,
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
                    ["doc"] = diagnosticDocumentName,
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
                    ["doc"] = diagnosticDocumentName,
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

    private static string GetDiagnosticDocumentName(
        string documentKey,
        string documentName,
        string databaseFilename)
    {
        foreach (string candidate in new[] { documentKey, documentName, databaseFilename })
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || string.Equals(candidate, "<null>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fileName = System.IO.Path.GetFileName(candidate);
            return string.IsNullOrWhiteSpace(fileName) ? candidate : fileName;
        }

        return "<unknown>";
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
            {
                LdFileHook.ClearRegisteredRedirectsForDocument(dbScope);
                ShpLoadHook.ResetDocumentDiagnostics();
            }
            if (clearTransientFileMappings)
                LdFileHook.ClearTransientRegisteredRedirects();
        }

        private static string FormatDbScope(IntPtr dbScope)
            => dbScope == IntPtr.Zero ? "0x0" : $"0x{dbScope.ToInt64():X}";
    }

    private static int MarkAffectedTextGraphicsModified(
        Database db,
        List<FontCheckResult> missingFonts)
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
    /// DEBUG 诊断：Regen 前读回样式表，确认写回结果已进入数据库。
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

