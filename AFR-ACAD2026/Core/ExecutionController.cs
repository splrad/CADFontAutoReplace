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

                // 第二阶段: Replace → Regen → Replace
                //   首次替换: 修正样式表，使 Regen 渲染时引用有效字体
                //   Regen:    刷新显示（LdFileHook 重定向确保渲染正确）
                //             副作用 — MText 内联码可能将缺失字体名写回样式表
                //   二次替换: 修复 Regen 对样式表的覆盖，确保最终状态正确
                FontReplacer.ReplaceMissingFonts(
                    doc.Database, missingFonts, config.MainFont, config.BigFont, config.TrueTypeFont);

                doc.Editor.Regen();

                var postRegenMissing = FontDetector.DetectMissingFonts(doc.Database);
                if (postRegenMissing.Count > 0)
                {
                    FontReplacer.ReplaceMissingFonts(
                        doc.Database, postRegenMissing, config.MainFont, config.BigFont, config.TrueTypeFont);
                }

                // 第三阶段: 收集 Hook 重定向记录（过滤样式表缺失字体，仅保留 MText 内联字体）
                // 排除集合并两次检测中确认缺失的字体（均由 FontReplacer 处理），
                // 不排除存在的字体，避免误过滤 MText 内联字体（如 @gbcbig → gbcbig）。
                var styleMissingFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < missingFonts.Count; i++)
                {
                    var f = missingFonts[i];
                    if (f.IsMainFontMissing && !string.IsNullOrEmpty(f.FileName))
                        styleMissingFonts.Add(f.FileName);
                    if (f.IsBigFontMissing && !string.IsNullOrEmpty(f.BigFontFileName))
                        styleMissingFonts.Add(f.BigFontFileName);
                }
                for (int i = 0; i < postRegenMissing.Count; i++)
                {
                    var f = postRegenMissing[i];
                    if (f.IsMainFontMissing && !string.IsNullOrEmpty(f.FileName))
                        styleMissingFonts.Add(f.FileName);
                    if (f.IsBigFontMissing && !string.IsNullOrEmpty(f.BigFontFileName))
                        styleMissingFonts.Add(f.BigFontFileName);
                }
                var inlineFixResults = LdFileHook.GetRedirectRecords(styleMissingFonts);
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
}
