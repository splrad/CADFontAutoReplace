using Autodesk.AutoCAD.ApplicationServices.Core;
using AFR.Abstractions;
using AFR.Models;

namespace AFR.Services;

/// <summary>
/// 日志分类枚举。
/// 数值越小输出优先级越高。
/// </summary>
internal enum LogCategory
{
    Error = 0,
    Warning = 1,       // 警告
    Info = 2,          // 一般信息
    Statistics = 3     // 统计汇总
}

/// <summary>
/// 日志服务，负责收集并输出日志到 AutoCAD 命令行。
/// 先缓冲、后按分类一次性输出，避免执行过程中频繁刷命令行。
/// </summary>
internal sealed class LogService : ILogService
{
    private static readonly Lazy<LogService> _instance = new(() => new LogService());
    /// <summary>全局日志服务实例。</summary>
    public static LogService Instance => _instance.Value;

    // Flush 时按分类排序后统一输出。
    private readonly List<(LogCategory Category, string Message, DateTime Timestamp)> _buffer = [];
    // 必须压在本轮命令行输出最后的条目。
    private readonly List<(LogCategory Category, string Message)> _tailBuffer = [];
    // 每个文档只显示一次版本横幅。
    private readonly HashSet<string> _headerShownDocuments = new(StringComparer.OrdinalIgnoreCase);
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    private LogService() { }

    /// <summary>记录一条信息级别日志到缓冲区。</summary>
    public void Info(string message) => AddEntry(LogCategory.Info, message);
    /// <summary>记录一条必须在本轮 Flush 最后输出的信息。</summary>
    public void InfoLast(string message)
    {
        AddTailEntry(LogCategory.Info, message);
    }
    /// <summary>记录一条警告级别日志到缓冲区。</summary>
    public void Warning(string message) => AddEntry(LogCategory.Warning, message);
    /// <summary>记录一条必须在本轮 Flush 最后输出的警告。</summary>
    public void WarningLast(string message)
    {
        AddTailEntry(LogCategory.Warning, message);
    }
    /// <summary>记录一条错误级别日志到缓冲区。</summary>
    public void Error(string message) => AddEntry(LogCategory.Error, message);
    /// <summary>记录一条包含异常信息的错误级别日志到缓冲区。会自动拼接异常消息。</summary>
    /// <param name="message">错误描述。</param>
    /// <param name="ex">触发错误的异常对象，其 Message 会被拼接到日志中。</param>
    public void Error(string message, Exception ex) =>
        AddEntry(LogCategory.Error, $"{message}: {ex.Message}");

    /// <summary>
    /// 统计缺失字体数量并生成汇总消息写入缓冲区。
    /// <para>
    /// 自动执行链路使用：按原始缺失和二次检测结果计算成功替换数量。
    /// </para>
    /// </summary>
    /// <param name="missingFonts">字体检查结果列表，包含每个字体样式的缺失情况。</param>
    /// <param name="stillMissingFonts">替换后仍然缺失的字体列表，为 null 或空表示全部替换成功。</param>
    /// <param name="runtimeMappingCount">文件级 Hook 实际命中的运行时映射数量。</param>
    public void AddStatistics(
        IReadOnlyList<FontCheckResult> missingFonts,
        IReadOnlyList<FontCheckResult>? stillMissingFonts = null,
        int runtimeMappingCount = 0)
    {
        (int missingShxMain, int missingShxBig, int missingTrueType) = CountMissingSlots(missingFonts);
        (int stillMissingShxMain, int stillMissingShxBig, int stillMissingTrueType) = CountMissingSlots(stillMissingFonts ?? []);

        int trueTypeCount = Math.Max(0, missingTrueType - stillMissingTrueType);
        int shxCount = Math.Max(0, missingShxMain - stillMissingShxMain);
        int bigFontCount = Math.Max(0, missingShxBig - stillMissingShxBig);
        int total = trueTypeCount + shxCount + bigFontCount;
        runtimeMappingCount = Math.Max(0, runtimeMappingCount);

        string msg = $"[字体修复]已替换缺失字体 {total} 个(SHX主字体: {shxCount} , SHX大字体: {bigFontCount} , TrueType: {trueTypeCount})";
        msg += $" | MText内联字体映射：{runtimeMappingCount}";

        lock (_lock)
        {
            _buffer.Add((LogCategory.Statistics, msg, DateTime.Now));
        }

        // 仍缺失时提醒用户走 AFRLOG 手动处理。
        int stillMissingTotal = stillMissingTrueType + stillMissingShxMain + stillMissingShxBig;
        if (stillMissingTotal > 0)
            WarningLast($"仍有 {stillMissingTotal} 个字体未成功替换，请执行 AFRLOG 手动指定替换字体");
    }

    private static (int ShxMain, int ShxBig, int TrueType) CountMissingSlots(
        IReadOnlyList<FontCheckResult> missingFonts)
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

            if (item.IsBigFontMissing && !item.IsTrueType)
                bigFontCount++;
        }

        return (shxCount, bigFontCount, trueTypeCount);
    }

    /// <summary>
    /// 根据实际执行的替换指令列表生成统计消息并写入缓冲区。
    /// <para>
    /// AFR 重新配置和 AFRLOG 手动替换使用：只统计本次实际提交的替换项。
    /// </para>
    /// </summary>
    /// <param name="replacements">本次实际执行的替换指令列表。</param>
    /// <param name="stillMissingCount">替换后仍然缺失的字体数量，为 0 表示全部替换成功。</param>
    public void AddReplacementStatistics(IReadOnlyList<StyleFontReplacement> replacements, int stillMissingCount = 0)
    {
        int shxCount = 0, bigFontCount = 0, trueTypeCount = 0;
        for (int i = 0; i < replacements.Count; i++)
        {
            var r = replacements[i];
            if (r.IsTrueType)
            {
                if (!string.IsNullOrEmpty(r.MainFontReplacement)) trueTypeCount++;
            }
            else
            {
                if (!string.IsNullOrEmpty(r.MainFontReplacement)) shxCount++;
                if (!string.IsNullOrEmpty(r.BigFontReplacement)) bigFontCount++;
            }
        }

        int total = shxCount + bigFontCount + trueTypeCount;
        if (total == 0) return;

        var parts = new List<string>(3);
        if (shxCount > 0) parts.Add($"SHX主字体:{shxCount}");
        if (bigFontCount > 0) parts.Add($"SHX大字体:{bigFontCount}");
        if (trueTypeCount > 0) parts.Add($"TrueType:{trueTypeCount}");

        int replaced = total - stillMissingCount;
        string msg;
        if (stillMissingCount > 0)
            msg = $"[字体修复]重新替换缺失字体 {total} 个，成功 {replaced} 个({string.Join(",", parts)})";
        else
            msg = $"[字体修复]已重新替换缺失字体 {total} 个({string.Join(",", parts)})";

        lock (_lock)
        {
            _buffer.Add((LogCategory.Statistics, msg, DateTime.Now));
        }

        if (stillMissingCount > 0)
            WarningLast($"仍有 {stillMissingCount} 个字体未成功替换，请执行 AFRLOG 手动指定替换字体");
    }

    /// <summary>
    /// 将一条日志条目添加到缓冲区。
    /// </summary>
    /// <param name="category">日志分类，决定输出时的排序位置和显示前缀。</param>
    /// <param name="message">日志消息内容。</param>
    private void AddEntry(LogCategory category, string message)
    {
        lock (_lock)
        {
            _buffer.Add((category, message, DateTime.Now));
        }
    }

    private void AddTailEntry(LogCategory category, string message)
    {
        lock (_lock)
        {
            _tailBuffer.Add((category, message));
        }
    }

    /// <summary>
    /// 重置指定文档的日志头显示状态。
    /// <para>
    /// 文档关闭时调用，便于重新打开同名图纸后再次显示横幅。
    /// </para>
    /// </summary>
    /// <param name="documentName">AutoCAD 文档的名称（即文件路径），不区分大小写。</param>
    public void ResetHeaderForDocument(string documentName)
    {
        lock (_lock)
        {
            _headerShownDocuments.Remove(documentName);
        }
    }

    /// <summary>
    /// 将缓冲区中的所有日志一次性输出到 AutoCAD 命令行，然后清空缓冲区。
    /// <para>
    /// 没有活动编辑器时保留缓冲区，等待下次 Flush。
    /// </para>
    /// </summary>
    public void Flush()
    {
        try
        {
            // 没有活动编辑器时不清空缓冲区。
            var doc = Application.DocumentManager.MdiActiveDocument;
            var editor = doc?.Editor;
            if (editor == null) return;

            // 桶索引对应 LogCategory 数值，天然按优先级输出。
            const int bucketCount = 4;
            List<string>?[] buckets = new List<string>?[bucketCount];
            List<(LogCategory Category, string Message)>? tail = null;
            bool showHeader;
            string docName;

            lock (_lock)
            {
                if (_buffer.Count == 0 && _tailBuffer.Count == 0) return;
                for (int i = 0; i < _buffer.Count; i++)
                {
                    var (category, message, _) = _buffer[i];
                    int idx = (int)category;
                    // Statistics 自带业务前缀，不再追加日志级别。
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
                if (_tailBuffer.Count > 0)
                {
                    tail = [.. _tailBuffer];
                    _tailBuffer.Clear();
                }

                docName = doc?.Name ?? string.Empty;
                showHeader = _headerShownDocuments.Add(docName);
            }

            if (showHeader)
            {
                // AFRUNLOAD 是隐藏维护入口：日志头可展示，但不通过 CommandMethod 注册进 CAD 补全体系。
                const string commandsLine = "\n命令: AFR(配置) AFRLOG(日志) AFRUNLOAD(卸载命令)";
                editor.WriteMessage(
                    "\n==========================================================================" +
                    $"\nAFR 缺失字体自动替换 v{PluginVersionService.GetDisplayVersionWithBuildMarker()}" +
                    "\n作者: splrad 秋夕寻星" +
                    "\n项目地址GitHub(国外)：github.com/splrad/CADFontAutoReplace" +
                    "\n项目地址Gitee(国内)：gitee.com/splrad/CADFontAutoReplace" +
                    commandsLine +
                    "\n==========================================================================");
            }

            for (int b = 0; b < bucketCount; b++)
            {
                var bucket = buckets[b];
                if (bucket == null) continue;
                for (int i = 0; i < bucket.Count; i++)
                {
                    editor.WriteMessage(bucket[i]);
                }
            }

            if (tail is not null)
            {
                for (int i = 0; i < tail.Count; i++)
                {
                    var (category, message) = tail[i];
                    string formatted = category switch
                    {
                        LogCategory.Error => $"\n[错误] {message}",
                        LogCategory.Warning => $"\n[警告] {message}",
                        LogCategory.Info => $"\n[信息] {message}",
                        _ => $"\n{message}"
                    };
                    editor.WriteMessage(formatted);
                }
            }

            // 追加换行以触发 AutoCAD 命令行刷新。
            editor.WriteMessage("\n");
        }
        catch
        {
            // 编辑器可能在 Flush 过程中失效，日志失败不应影响插件流程。
        }
    }
}
