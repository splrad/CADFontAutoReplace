using Autodesk.AutoCAD.ApplicationServices.Core;
using AFR.Abstractions;
using AFR.Models;

namespace AFR.Services;

/// <summary>
/// 日志优先级分类，数值越小优先级越高，越先输出。
/// </summary>
internal enum LogCategory
{
    Error = 0,         // 错误 — 最高优先级
    Warning = 1,       // 警告
    Info = 2,          // 一般信息
    Statistics = 3     // 统计汇总 — 最后输出
}

/// <summary>
/// 缓冲日志服务，支持优先级排序与延迟统一输出到 AutoCAD 命令行。
/// 所有日志先写入缓存，执行完成后通过 Flush 按优先级一次性输出。
/// 线程安全的单例模式。
/// </summary>
internal sealed class LogService : ILogService
{
    private static readonly Lazy<LogService> _instance = new(() => new LogService());
    public static LogService Instance => _instance.Value;

    private readonly List<(LogCategory Category, string Message, DateTime Timestamp)> _buffer = [];
    private readonly HashSet<string> _headerShownDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private LogService() { }

    public void Info(string message) => AddEntry(LogCategory.Info, message);
    public void Warning(string message) => AddEntry(LogCategory.Warning, message);
    public void Error(string message) => AddEntry(LogCategory.Error, message);
    public void Error(string message, Exception ex) =>
        AddEntry(LogCategory.Error, $"{message}: {ex.Message}");

    /// <summary>
    /// 根据缺失字体检测结果直接计算并添加统计条目。
    /// 必须在 Flush 之前调用。
    /// </summary>
    public void AddStatistics(IReadOnlyList<FontCheckResult> missingFonts, int mtextMappingCount = 0)
    {
        int trueTypeCount = 0, shxCount = 0, bigFontCount = 0;
        for (int i = 0; i < missingFonts.Count; i++)
        {
            var item = missingFonts[i];
            if (item.IsMainFontMissing)
            {
                if (item.IsTrueType) trueTypeCount++;
                else shxCount++;
            }
            // TrueType 样式不支持大字体，不计入统计
            if (item.IsBigFontMissing && !item.IsTrueType) bigFontCount++;
        }
        int total = trueTypeCount + shxCount + bigFontCount;

        string msg = $"[字体修复]已替换缺失字体 {total} 个（SHX主字体：{shxCount}，SHX大字体：{bigFontCount}，TrueType：{trueTypeCount}）";
        if (mtextMappingCount > 0)
            msg += $" | MText内联字体映射：{mtextMappingCount}";

        lock (_lock)
        {
            _buffer.Add((LogCategory.Statistics, msg, DateTime.Now));
        }
    }

    private void AddEntry(LogCategory category, string message)
    {
        lock (_lock)
        {
            _buffer.Add((category, message, DateTime.Now));
        }
    }

    /// <summary>
    /// 清除指定文档的日志头显示标记。
    /// 文档关闭后重新打开时可再次显示日志头。
    /// </summary>
    public void ResetHeaderForDocument(string documentName)
    {
        lock (_lock)
        {
            _headerShownDocuments.Remove(documentName);
        }
    }

    /// <summary>
    /// 将所有缓冲日志按优先级排序后统一输出到 AutoCAD 编辑器命令行。
    /// 输出顺序：日志头 → 错误 → 警告 → TrueType → SHX → 大字体 → 信息 → 统计。
    /// </summary>
    public void Flush()
    {
        try
        {
            // 先检查编辑器是否可用，不可用则保留缓冲区等待下次 Flush
            var doc = Application.DocumentManager.MdiActiveDocument;
            var editor = doc?.Editor;
            if (editor == null) return;

            // 分桶收集 — 避免 OrderBy 排序分配
            const int bucketCount = 4;
            List<string>?[] buckets = new List<string>?[bucketCount];
            bool showHeader;
            string docName;

            lock (_lock)
            {
                if (_buffer.Count == 0) return;
                for (int i = 0; i < _buffer.Count; i++)
                {
                    var (category, message, _) = _buffer[i];
                    int idx = (int)category;
                    string formatted = category switch
                    {
                        LogCategory.Error => $"\n[错误] {message}",
                        LogCategory.Warning => $"\n[警告] {message}",
                        LogCategory.Info => $"\n[信息] {message}",
                        _ => $"\n{message}"
                    };
                    (buckets[idx] ??= []).Add(formatted);
                }
                _buffer.Clear();

                // 日志头判定在同一把锁内完成，消除竞争窗口
                docName = doc?.Name ?? string.Empty;
                showHeader = _headerShownDocuments.Add(docName);
            }

            if (showHeader)
            {
                editor.WriteMessage(
                    "\n==========================================================================" +
                    "\nAFR 缺失字体自动替换 v3.0" +
                    "\ngithub.com/splrad/CADFontAutoReplace | gitee.com/splrad/CADFontAutoReplace" +
                    "\n命令: AFR(配置) AFRLOG(日志) AFRUNLOAD(卸载)" +
                    "\n==========================================================================");
            }

            // 按优先级桶顺序输出
            for (int b = 0; b < bucketCount; b++)
            {
                var bucket = buckets[b];
                if (bucket == null) continue;
                for (int i = 0; i < bucket.Count; i++)
                {
                    editor.WriteMessage(bucket[i]);
                }
            }
        }
        catch
        {
            // 抑制异常 — 编辑器不可用
        }
    }
}
