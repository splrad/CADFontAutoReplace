using Autodesk.AutoCAD.ApplicationServices.Core;

namespace AFR_ACAD2026.Services;

/// <summary>
/// 日志优先级分类，数值越小优先级越高，越先输出。
/// </summary>
internal enum LogCategory
{
    Error = 0,         // 错误 — 最高优先级
    Warning = 1,       // 警告
    Info = 2,          // 一般信息
    FontTrueType = 3,  // TrueType 字体替换记录（第一优先级）
    FontShx = 4,       // SHX 字体替换记录（第二优先级）
    FontBigFont = 5,   // 大字体替换记录（第三优先级）
    Statistics = 6     // 统计汇总 — 最后输出
}

/// <summary>
/// 缓冲日志服务，支持优先级排序与延迟统一输出到 AutoCAD 命令行。
/// 所有日志先写入缓存，执行完成后通过 Flush 按优先级一次性输出。
/// 线程安全的单例模式。
/// </summary>
internal sealed class LogService
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

    /// <summary>记录 TrueType 字体替换（第一优先级）。</summary>
    public void FontTrueType(string message) => AddEntry(LogCategory.FontTrueType, message);

    /// <summary>记录 SHX 字体替换（第二优先级）。</summary>
    public void FontShx(string message) => AddEntry(LogCategory.FontShx, message);

    /// <summary>记录大字体替换（第三优先级）。</summary>
    public void FontBigFont(string message) => AddEntry(LogCategory.FontBigFont, message);

    /// <summary>
    /// 根据已缓存的字体替换记录自动计算并添加统计条目。
    /// 必须在 Flush 之前调用。
    /// </summary>
    public void AddStatistics()
    {
        lock (_lock)
        {
            int trueTypeCount = _buffer.Count(e => e.Category == LogCategory.FontTrueType);
            int shxCount = _buffer.Count(e => e.Category == LogCategory.FontShx);
            int bigFontCount = _buffer.Count(e => e.Category == LogCategory.FontBigFont);
            int total = trueTypeCount + shxCount + bigFontCount;

            _buffer.Add((LogCategory.Statistics,
                $"替换TrueType字体：{trueTypeCount}；替换SHX字体：{shxCount}；替换BigFont字体：{bigFontCount}；",
                DateTime.Now));
            _buffer.Add((LogCategory.Statistics,
                $"共替换缺失字体数量：{total}",
                DateTime.Now));
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

            List<(LogCategory Category, string Message, DateTime Timestamp)> entries;
            lock (_lock)
            {
                if (_buffer.Count == 0) return;
                entries = [.. _buffer.OrderBy(e => (int)e.Category)];
                _buffer.Clear();
            }

            // 日志头 — 每个图纸仅显示一次
            var docName = doc?.Name ?? string.Empty;
            bool showHeader;
            lock (_lock)
            {
                showHeader = _headerShownDocuments.Add(docName);
            }

            if (showHeader)
            {
                editor.WriteMessage("\n=============================================");
                editor.WriteMessage("\nCAD缺失字体自动替换工具 AFR");
                editor.WriteMessage("\n版本：v2.0-2026/03/21");
                editor.WriteMessage("\n插件首次加载运行必须执行：AFR");
                editor.WriteMessage("\n命令说明：");
                editor.WriteMessage("\n AFR - 配置替换字体");
                editor.WriteMessage("\n AFRUNLOAD - 卸载插件");
                editor.WriteMessage("\n=============================================");
            }

            // 按优先级排序输出所有缓存条目
            foreach (var (category, message, _) in entries)
            {
                switch (category)
                {
                    case LogCategory.Error:
                        editor.WriteMessage($"\n[错误] {message}");
                        break;
                    case LogCategory.Warning:
                        editor.WriteMessage($"\n[警告] {message}");
                        break;
                    case LogCategory.FontTrueType:
                    case LogCategory.FontShx:
                    case LogCategory.FontBigFont:
                    case LogCategory.Statistics:
                        editor.WriteMessage($"\n{message}");
                        break;
                    case LogCategory.Info:
                        editor.WriteMessage($"\n[信息] {message}");
                        break;
                }
            }

            editor.WriteMessage("\n");
        }
        catch
        {
            // 抑制异常 — 编辑器不可用
        }
    }
}
