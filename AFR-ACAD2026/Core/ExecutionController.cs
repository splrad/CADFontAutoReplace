using Autodesk.AutoCAD.ApplicationServices;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.Core;

/// <summary>
/// 统一执行控制器，负责字体检测与替换流程。
/// 处理所有触发来源: Startup、Command、DocumentCreated、DocumentActivated。
/// 包含节流、重复执行防护以及 IsInitialized 门控。
/// </summary>
internal sealed class ExecutionController
{
    private static readonly Lazy<ExecutionController> _instance = new(() => new ExecutionController());
    public static ExecutionController Instance => _instance.Value;

    private readonly object _throttleLock = new();
    private DateTime _lastExecutionTime = DateTime.MinValue;
    private const int ThrottleMilliseconds = 500;

    private ExecutionController() { }

    /// <summary>
    /// 对指定文档执行字体检测与替换。
    /// 遵守 IsInitialized 门控、重复执行防护和节流机制。
    /// </summary>
    public void Execute(Document doc, string triggerSource)
    {
        if (doc == null) return;

        var log = LogService.Instance;
        var config = ConfigService.Instance;

        try
        {
            // 节流: 防止频繁触发
            lock (_throttleLock)
            {
                var now = DateTime.Now;
                if ((now - _lastExecutionTime).TotalMilliseconds < ThrottleMilliseconds)
                {
                    log.Info($"执行已节流: {triggerSource}");
                    return;
                }
                _lastExecutionTime = now;
            }

            // 门控: 仅在已初始化时自动执行
            if (!config.IsInitialized)
            {
                log.Info($"未初始化 — 已跳过 ({triggerSource})。");
                return;
            }

            // 重复执行防护
            var contextMgr = DocumentContextManager.Instance;
            if (contextMgr.HasExecuted(doc))
            {
                log.Info($"已处理过: {doc.Name}");
                return;
            }

            log.Info($"正在处理 '{doc.Name}' (触发源: {triggerSource})");

            // 获取文档写入锁
            using (doc.LockDocument())
            {
                // 第一阶段: 检测缺失字体
                var detector = new FontDetector();
                var missingFonts = detector.DetectMissingFonts(doc.Database);

                if (missingFonts.Count == 0)
                {
                    log.Info("未检测到缺失字体。");
                    contextMgr.MarkExecuted(doc);
                    return;
                }

                // 第二阶段: 替换缺失字体
                var replacer = new FontReplacer();
                int replaceCount = replacer.ReplaceMissingFonts(
                    doc.Database, missingFonts, config.MainFont, config.BigFont);

                // 添加统计汇总
                log.AddStatistics();

                // 第三阶段: 重新生成图形
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
