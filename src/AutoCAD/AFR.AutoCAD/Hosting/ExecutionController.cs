using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.FontMapping;
using AFR.Models;
using AFR.Services;

namespace AFR.Hosting;

/// <summary>
/// 统一执行控制器，负责字体检测与替换流程。
/// 处理触发来源: Startup、Command、DocumentCreated。
/// 包含重复执行防护以及 IsInitialized 门控。
/// </summary>
internal sealed class ExecutionController
{
    private static readonly Lazy<ExecutionController> _instance = new(() => new ExecutionController());
    public static ExecutionController Instance => _instance.Value;

    private ExecutionController() { }

    /// <summary>
    /// 对指定文档执行字体检测与替换。
    /// 遵守 IsInitialized 门控和重复执行防护。
    /// </summary>
    public void Execute(Document doc, string triggerSource)
    {
        if (doc == null || doc.IsDisposed) return;

        var log = LogService.Instance;
        var config = ConfigService.Instance;
        bool summarized = false;

        try
        {
            // 门控: 仅在已初始化时自动执行
            if (!config.IsInitialized)
            {
                log.Info("请输入 AFR 命令配置替换字体。");
                return;
            }

            // 重复执行防护
            var contextMgr = DocumentContextManager.Instance;
            if (contextMgr.HasExecuted(doc)) return;

            // 获取文档写入锁
            using (doc.LockDocument())
            {
                // 创建独立的执行上下文 — 缓存生命周期与本次事务绑定，GC 自动回收
                var context = new FontDetectionContext(doc.Database);

                DiagnosticLogger.BeginDocument(doc.Name, config.MainFont, config.BigFont, config.TrueTypeFont);

                // 第一阶段: 检测缺失字体（样式表原始状态）
                DiagnosticLogger.BeginPhase("检测缺失字体");
                var missingFonts = FontDetector.DetectMissingFonts(context);

                // 存储检测结果供 AFRLOG 命令使用
                contextMgr.StoreDetectionResults(doc, missingFonts);
                DiagnosticLogger.EndPhase($"缺失: {missingFonts.Count}个");

                if (missingFonts.Count == 0)
                {
                    log.Info("未检测到缺失字体。");
                    contextMgr.MarkExecuted(doc);
                    DiagnosticLogger.WriteSummary();
                    summarized = true;
                    return;
                }

                // 第二阶段: 替换缺失字体 + Regen 刷新显示
                DiagnosticLogger.BeginPhase("替换缺失字体");
                int replaceCount = FontReplacer.ReplaceMissingFonts(
                    missingFonts, config.MainFont, config.BigFont, config.TrueTypeFont, context);
                DiagnosticLogger.EndPhase($"替换: {replaceCount}个");

                // 替换后二次检测：确认哪些字体仍然缺失（替换字体不可用时会发生）
                // 使用全新 context 避免缓存干扰
                var postContext = new FontDetectionContext(doc.Database);
                var stillMissing = FontDetector.DetectMissingFonts(postContext);
                contextMgr.StoreStillMissingResults(doc, stillMissing);
                DiagnosticLogger.Log("验证", $"替换后仍缺失: {stillMissing.Count}个");

                // 计算未替换的字体槽位数（主字体+大字体）
                int stillMissingSlotCount = 0;
                for (int i = 0; i < stillMissing.Count; i++)
                {
                    if (stillMissing[i].IsMainFontMissing) stillMissingSlotCount++;
                    if (stillMissing[i].IsBigFontMissing && !stillMissing[i].IsTrueType) stillMissingSlotCount++;
                }

                // CleanupStaleShxReferences 仅在 Hook 启用时需要（防止 Hook 重定向导致内部状态不一致）
                FontReplacer.CleanupStaleShxReferences(context);

                // 诊断: Regen 前验证样式表状态（确认替换是否持久化到数据库）
                DiagnosticLogger.BeginPhase("验证替换结果");
                VerifyStyleTableAfterReplace(doc.Database, missingFonts);
                DiagnosticLogger.EndPhase();

                // Regen 刷新显示 — 使替换后的字体立即可见
                doc.Editor.Regen();

                // 第三阶段: 扫描 MText 内联字体，交叉比对 Hook 重定向记录
                // 正向扫描法: 解析 MText.Contents 中的 \F/\f 格式代码，
                // 与 Hook 重定向记录交叉比对，精确识别被修复的内联字体。
                DiagnosticLogger.BeginPhase("扫描MText内联字体");
                var inlineFonts = MTextInlineFontScanner.ScanInlineFonts(doc.Database);
                var redirectLog = LdFileHook.GetRawRedirectLog();

                // 诊断: 记录交叉比对的两侧数据，便于排查匹配失败原因
                if (inlineFonts.Count > 0)
                {
                    foreach (var (name, type) in inlineFonts)
                        DiagnosticLogger.Log("MText内联", $"扫描到: '{name}' 类型={type}");
                }
                if (redirectLog.Count > 0)
                {
                    foreach (var (key, (rep, ft)) in redirectLog)
                        DiagnosticLogger.Log("MText内联", $"重定向记录: '{key}' → '{rep}' param2={ft}");
                }

                var inlineFixResults = BuildInlineFixRecords(inlineFonts, redirectLog);
                contextMgr.StoreInlineFontFixResults(doc, inlineFixResults);
                DiagnosticLogger.EndPhase($"内联字体: {inlineFonts.Count}个, 修复: {inlineFixResults.Count}个");

                // 统计汇总 — Regen 之后输出，确保统计信息是最后一行实质内容
                log.AddStatistics(missingFonts, stillMissingSlotCount, inlineFixResults.Count);
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

    /// <summary>
    /// 交叉比对 MText 内联字体引用与 Hook 重定向记录，
    /// 构建精确的内联字体修复记录。
    /// 仅返回同时满足以下条件的记录:
    ///   1. 在 MText 内联字体引用中出现（正向识别）
    ///   2. 在 Hook 重定向记录中存在（确认被修复）
    /// </summary>
    private static List<InlineFontFixRecord> BuildInlineFixRecords(
        Dictionary<string, InlineFontType> inlineFonts,
        IReadOnlyDictionary<string, (string Replacement, int FontType)> redirectLog)
    {
        var records = new List<InlineFontFixRecord>();

        foreach (var (fontName, inlineType) in inlineFonts)
        {
            if (!redirectLog.TryGetValue(fontName, out var redirect))
                continue;

            string category = inlineType switch
            {
                InlineFontType.ShxBigFont => "SHX大字体",
                InlineFontType.TrueType => "TrueType",
                _ => "SHX主字体"
            };

            records.Add(new InlineFontFixRecord(fontName, redirect.Replacement, "MText内联", category));
        }

        return records;
    }

    /// <summary>
    /// 诊断: 在 Regen 前读回样式表，验证 FontReplacer 的修改是否已写入数据库。
    /// </summary>
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
}
