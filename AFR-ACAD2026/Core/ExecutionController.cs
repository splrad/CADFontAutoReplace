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

                // 第二阶段: 替换缺失字体
                // 不执行 Regen — 原因：
                //   Regen 处理 MText 内联码时会将缺失字体名（如 @Arial Unicode MS.shx）
                //   写回样式表并缓存到 AutoCAD 内部状态，无论之后 Replace 多少次，
                //   内部状态始终与数据库不一致，导致 ST 对话框弹出"当前样式已修改"。
                // 不需要 Regen 的理由：
                //   SHX 字体：LdFileHook 已在 DWG 加载阶段将缺失字体重定向到替换字体，
                //             渲染结果已正确，无需 Regen 刷新显示。
                //   TrueType：下次用户交互（缩放/平移）触发自动 Regen 时显示更新。
                FontReplacer.ReplaceMissingFonts(
                    doc.Database, missingFonts, config.MainFont, config.BigFont, config.TrueTypeFont);

                // 第三阶段: 收集 Hook 重定向记录（过滤样式表缺失字体，仅保留 MText 内联字体）
                // 排除集仅包含样式表中确认缺失的字体（由 FontReplacer 处理），
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
