using Autodesk.AutoCAD.ApplicationServices;
using AFR_ACAD2026.FontMapping;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.Core;

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
                // 第一阶段: 检测缺失字体（样式表）
                var missingFonts = FontDetector.DetectMissingFonts(doc.Database);

                // 存储检测结果供 AFRLOG 命令使用
                contextMgr.StoreDetectionResults(doc, missingFonts);

                if (missingFonts.Count == 0)
                {
                    log.Info("未检测到缺失字体。");
                    contextMgr.MarkExecuted(doc);
                    return;
                }

                // 第二阶段: 收集样式表原始字体名（用于后续过滤 Hook 记录）
                // 必须在 FontReplacer 修改样式表之前收集
                var styleTableFontNames = FontDetector.CollectStyleTableFontNames(doc.Database);

                // 第三阶段: 替换样式表中的缺失字体
                int replaceCount = FontReplacer.ReplaceMissingFonts(
                    doc.Database, missingFonts, config.MainFont, config.BigFont, config.TrueTypeFont);

                // 第四阶段: 收集 Hook 重定向记录（过滤样式表字体，仅保留 MText 内联字体）
                // Hook 在 DWG 解析阶段已重定向所有缺失字体（含样式表字体），
                // 但样式表字体已由 FontReplacer 覆盖，用户可通过 ST/AFRLOG 随时调整，
                // 此处过滤掉样式表字体，仅在 AFRLOG 中显示 MText 内联字体映射。
                var inlineFixResults = LdFileHook.GetRedirectRecords(styleTableFontNames);
                contextMgr.StoreInlineFontFixResults(doc, inlineFixResults);

                // 添加统计汇总
                log.AddStatistics(missingFonts, inlineFixResults.Count);

                // 第四阶段: 重新生成图形
                if (replaceCount > 0)
                {
                    doc.Editor.Regen();
                }
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
}
