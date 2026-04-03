using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
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

        try
        {
            // 门控: 仅在已初始化时自动执行
            if (!config.IsInitialized)
            {
                log.Info("请先执行 AFR 命令配置替换字体，插件才会自动替换缺失字体。");
                return;
            }

            // 重复执行防护
            var contextMgr = DocumentContextManager.Instance;
            if (contextMgr.HasExecuted(doc)) return;

            // 获取文档写入锁
            using (doc.LockDocument())
            {
                // 第一阶段: 检测缺失字体（样式表原始状态）
                var missingFonts = FontDetector.DetectMissingFonts(doc.Database);

                // 存储检测结果供 AFRLOG 命令使用
                contextMgr.StoreDetectionResults(doc, missingFonts);

                if (missingFonts.Count == 0)
                {
                    log.Info("未检测到缺失字体。");
                    contextMgr.MarkExecuted(doc);
                    return;
                }

                // 第二阶段: 替换缺失字体 + Regen 刷新显示
                FontReplacer.ReplaceMissingFonts(
                    doc.Database, missingFonts, config.MainFont, config.BigFont, config.TrueTypeFont);

                // 诊断: Regen 前验证样式表状态（确认替换是否持久化到数据库）
                VerifyStyleTableAfterReplace(doc.Database, missingFonts, log);

                doc.Editor.Regen();

                // 第三阶段: 扫描 MText 内联字体，交叉比对 Hook 重定向记录
                // 正向扫描法: 解析 MText.Contents 中的 \F/\f 格式代码，
                // 与 Hook 重定向记录交叉比对，精确识别被修复的内联字体。
                var inlineFonts = MTextInlineFontScanner.ScanInlineFonts(doc.Database);
                var redirectLog = LdFileHook.GetRawRedirectLog();
                var inlineFixResults = BuildInlineFixRecords(inlineFonts, redirectLog);
                contextMgr.StoreInlineFontFixResults(doc, inlineFixResults);

                // 添加统计汇总
                log.AddStatistics(missingFonts, inlineFixResults.Count);
            }

            contextMgr.MarkExecuted(doc);
        }
        catch (Exception ex)
        {
            log.Error($"执行失败 ({triggerSource})", ex);
        }
        finally
        {
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
            // 归一化: redirect log 的 key 是 "name.shx" 小写格式
            string lookupKey = fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
                ? fontName.ToLowerInvariant()
                : fontName;

            if (!redirectLog.TryGetValue(lookupKey, out var redirect))
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
        Database db, IReadOnlyList<FontCheckResult> missingFonts, LogService log)
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
                    if (!missingNames.Contains(style.Name)) continue;

                    var font = style.Font;
                    log.Info($"[验证] 样式='{style.Name}' TypeFace='{font.TypeFace}' FileName='{style.FileName}' BigFont='{style.BigFontFileName}' CharSet={font.CharacterSet} Pitch={font.PitchAndFamily}");
                }
                catch { }
            }

            tr.Commit();
        }
        catch (Exception ex)
        {
            log.Warning($"[验证] 读回样式表失败: {ex.Message}");
        }
    }
}
