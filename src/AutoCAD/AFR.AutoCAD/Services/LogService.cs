using Autodesk.AutoCAD.ApplicationServices.Core;
using AFR.Abstractions;
using AFR.Models;

namespace AFR.Services;

/// <summary>
/// 日志分类枚举。
/// 数值越小优先级越高，Flush 输出时按数值从小到大排序，
/// 因此错误日志会排在最前面，统计汇总排在最后面。
/// </summary>
internal enum LogCategory
{
    Error = 0,         // 错误（优先级最高，最先输出）
    Warning = 1,       // 警告
    Info = 2,          // 一般信息
    Statistics = 3     // 统计汇总（优先级最低，最后输出）
}

/// <summary>
/// 日志服务，负责收集并输出日志到 AutoCAD 命令行。
/// <para>
/// 采用全局单例模式，通过 <see cref="Instance"/> 访问唯一实例。
/// </para>
/// <para>
/// 使用"先缓冲、后输出"策略：
/// <list type="number">
///   <item>调用 <see cref="Info"/>、<see cref="Warning"/>、<see cref="Error(string)"/> 将日志写入内存缓冲区；</item>
///   <item>调用 <see cref="Flush"/> 将缓冲区中的日志按「错误 → 警告 → 信息 → 统计」顺序一次性输出到 AutoCAD 命令行。</item>
/// </list>
/// 这样做是为了避免在替换过程中频繁写入命令行导致输出混乱。
/// </para>
/// </summary>
internal sealed class LogService : ILogService
{
    // 使用 Lazy<T> 实现线程安全的延迟初始化单例：首次访问 Instance 时才创建实例
    private static readonly Lazy<LogService> _instance = new(() => new LogService());
    /// <summary>获取 LogService 的全局唯一实例。</summary>
    public static LogService Instance => _instance.Value;

    // 日志缓冲区：暂存所有日志条目（分类 + 消息 + 时间戳），Flush 时统一排序输出
    private readonly List<(LogCategory Category, string Message, DateTime Timestamp)> _buffer = new List<(LogCategory, string, DateTime)>();
    // 记录已显示过 AFR 版本信息头的文档名，确保每个文档在整个会话中只显示一次横幅
    private readonly HashSet<string> _headerShownDocuments = new(StringComparer.OrdinalIgnoreCase);
    // 同步锁：保护 _buffer 和 _headerShownDocuments 的并发访问（AutoCAD 可能从不同线程触发日志写入）
    private readonly object _lock = new();

    // 私有构造函数：禁止外部 new，确保只能通过 Instance 属性获取实例
    private LogService() { }

    /// <summary>记录一条信息级别日志到缓冲区。</summary>
    /// <param name="message">日志内容。</param>
    public void Info(string message) => AddEntry(LogCategory.Info, message);
    /// <summary>记录一条警告级别日志到缓冲区。</summary>
    /// <param name="message">日志内容。</param>
    public void Warning(string message) => AddEntry(LogCategory.Warning, message);
    /// <summary>记录一条错误级别日志到缓冲区。</summary>
    /// <param name="message">日志内容。</param>
    public void Error(string message) => AddEntry(LogCategory.Error, message);
    /// <summary>记录一条包含异常信息的错误级别日志到缓冲区。会自动拼接异常消息。</summary>
    /// <param name="message">错误描述。</param>
    /// <param name="ex">触发错误的异常对象，其 Message 会被拼接到日志中。</param>
    public void Error(string message, Exception ex) =>
        AddEntry(LogCategory.Error, $"{message}: {ex.Message}");

    /// <summary>
    /// 统计缺失字体数量并生成汇总消息写入缓冲区。
    /// <para>
    /// 字体按三种类型分别计数：SHX 主字体、SHX 大字体、TrueType 字体。
    /// 若仍有未替换成功的字体，还会额外写入一条警告日志提示用户手动处理。
    /// 本方法应在 <see cref="Flush"/> 之前调用，否则统计信息不会包含在本次输出中。
    /// </para>
    /// </summary>
    /// <param name="missingFonts">字体检查结果列表，包含每个字体样式的缺失情况。</param>
    /// <param name="stillMissingCount">替换后仍然缺失的字体数量，为 0 表示全部替换成功。</param>
    /// <param name="mtextMappingCount">MText 多行文字中通过内联字体映射修复的数量。</param>
    public void AddStatistics(IReadOnlyList<FontCheckResult> missingFonts, int stillMissingCount = 0, int mtextMappingCount = 0)
    {
        // --- 第一步：遍历检查结果，按字体类型分别计数 ---
        int trueTypeCount = 0, shxCount = 0, bigFontCount = 0;
        for (int i = 0; i < missingFonts.Count; i++)
        {
            var item = missingFonts[i];
            // 主字体缺失时，根据字体类型计入 TrueType 或 SHX 计数
            if (item.IsMainFontMissing)
            {
                if (item.IsTrueType) trueTypeCount++;
                else shxCount++;
            }
            // 大字体是 SHX 特有概念，TrueType 样式不支持大字体，因此排除 TrueType
            if (item.IsBigFontMissing && !item.IsTrueType) bigFontCount++;
        }
        int total = trueTypeCount + shxCount + bigFontCount;
        int replaced = total - stillMissingCount;

        // --- 第二步：根据替换结果生成不同措辞的汇总消息 ---
        string msg;
        if (stillMissingCount > 0)
            msg = $"[字体修复]检测到缺失字体 {total} 个，已替换 {replaced} 个(SHX主字体:{shxCount},SHX大字体:{bigFontCount},TrueType:{trueTypeCount})";
        else
            msg = $"[字体修复]已替换缺失字体 {total} 个(SHX主字体:{shxCount},SHX大字体:{bigFontCount},TrueType:{trueTypeCount})";

        // MText 内联字体映射统计始终追加到汇总消息末尾
        msg += $" | MText内联字体映射：{mtextMappingCount}";

        // --- 第三步：将汇总消息写入缓冲区 ---
        lock (_lock)
        {
            _buffer.Add((LogCategory.Statistics, msg, DateTime.Now));
        }

        // 如果仍有未替换的字体，额外记录一条警告提醒用户手动处理
        if (stillMissingCount > 0)
            AddEntry(LogCategory.Warning, $"仍有 {stillMissingCount} 个字体未成功替换，请执行 AFRLOG 手动指定替换字体");
    }

    /// <summary>
    /// 根据实际执行的替换指令列表生成统计消息并写入缓冲区。
    /// <para>
    /// 与 <see cref="AddStatistics"/> 不同，本方法从 <see cref="StyleFontReplacement"/> 列表计数，
    /// 仅统计本次实际替换的类型，括号中只包含 count > 0 的类型。
    /// 用于 AFR 命令重新配置和 AFRLOG 手动替换的日志输出。
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

        // 动态拼接括号内容：仅包含本次实际替换的类型
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
            AddEntry(LogCategory.Warning, $"仍有 {stillMissingCount} 个字体未成功替换，请执行 AFRLOG 手动指定替换字体");
    }

    /// <summary>
    /// 将一条日志条目添加到缓冲区（线程安全）。
    /// 所有公开的 Info/Warning/Error 方法最终都通过此方法写入缓冲区。
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

    /// <summary>
    /// 重置指定文档的日志头显示状态。
    /// <para>
    /// 调用后，该文档下次执行 <see cref="Flush"/> 时会重新输出 AFR 版本信息横幅。
    /// 通常在文档关闭时调用，这样当用户重新打开同名文档时还能看到横幅。
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
    /// 输出顺序固定为：错误 → 警告 → 信息 → 统计汇总（由 <see cref="LogCategory"/> 数值决定）。
    /// 若当前没有活动文档或编辑器不可用，日志会保留在缓冲区，等待下次调用时再输出。
    /// </para>
    /// </summary>
    public void Flush()
    {
        try
        {
            // --- 第一步：获取当前活动文档的编辑器，用于向命令行写入文本 ---
            // 如果没有打开的文档（editor 为 null），直接返回，日志保留在缓冲区不丢失
            var doc = Application.DocumentManager.MdiActiveDocument;
            var editor = doc?.Editor;
            if (editor == null) return;

            // --- 第二步：将缓冲区中的日志按分类放入 4 个桶中 ---
            // 桶的索引对应 LogCategory 的数值：[0]=Error, [1]=Warning, [2]=Info, [3]=Statistics
            // 这样遍历桶时天然就是按优先级从高到低的顺序
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
                    // 根据分类添加中文前缀（Statistics 类型不加前缀，因为它自带格式）
                    string formatted = category switch
                    {
                        LogCategory.Error => $"\n[错误] {message}",
                        LogCategory.Warning => $"\n[警告] {message}",
                        LogCategory.Info => $"\n[信息] {message}",
                        _ => $"\n{message}"
                    };
                    // 如果该桶还没创建列表就先创建，然后再添加格式化后的消息
                    if (buckets[idx] == null) buckets[idx] = new List<string>();
                    buckets[idx].Add(formatted);
                }
                _buffer.Clear();

                // 尝试将当前文档名加入已显示集合，Add 返回 true 表示首次加入，需要显示横幅
                docName = doc?.Name ?? string.Empty;
                showHeader = _headerShownDocuments.Add(docName);
            }

            // --- 第三步：首次输出时显示 AFR 插件的版本信息横幅 ---
            if (showHeader)
            {
                editor.WriteMessage(
                    "\n==========================================================================" +
                    "\nAFR 缺失字体自动替换 v7.0" +
                    "\n项目地址GitHub(国外)：github.com/splrad/CADFontAutoReplace" +
                    "\n项目地址Gitee(国内)：gitee.com/splrad/CADFontAutoReplace" +
                    "\n命令: AFR(配置) AFRLOG(日志) AFRUNLOAD(卸载)" +
                    "\n==========================================================================");
            }

            // --- 第四步：按桶的索引顺序（即优先级）依次输出所有日志 ---
            for (int b = 0; b < bucketCount; b++)
            {
                var bucket = buckets[b];
                if (bucket == null) continue;
                for (int i = 0; i < bucket.Count; i++)
                {
                    editor.WriteMessage(bucket[i]);
                }
            }

            // 追加换行符，触发 AutoCAD 命令行刷新，确保日志文本立即可见
            editor.WriteMessage("\n");
        }
        catch
        {
            // Flush 过程中若编辑器突然不可用（如文档被关闭），静默忽略，避免插件崩溃
        }
    }
}
