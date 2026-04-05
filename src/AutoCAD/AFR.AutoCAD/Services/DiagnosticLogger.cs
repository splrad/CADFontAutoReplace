#if DEBUG

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace AFR.Services;

/// <summary>日志等级。</summary>
internal enum DiagLevel { Info, Warn, Error }

/// <summary>
/// 全场景内部追踪系统 — 记录插件全生命周期的诊断日志。
/// 涵盖启动/卸载、UI 交互、底层 API 通信、性能监测与异常追踪。
/// 仅在 DEBUG 编译模式下生效，Release 版本中所有调用被编译器自动移除。
///
/// 格式:  [Timestamp] [Level] [T:ThreadId] [Source.Caller] Message | ContextKey=Value ...
/// 输出:  插件目录下的 AFR_Diag_*.log 文件（单文件上限 10MB 自动分包，保留 7 天）
/// 启用:  在 PluginEntryBase.Initialize() 中调用 DiagnosticLogger.Enable()
/// </summary>
internal static class DiagnosticLogger
{
    // ── 常量 ──
    private const long MaxFileSize = 10L * 1024 * 1024; // 10MB
    private const int RetentionDays = 7;
    private const string FilePrefix = "AFR_Diag_";
    private const string FilePattern = "AFR_Diag_*.log";
    private static readonly string FlushSentinel = new('\0', 1); // 引用相等标记

    // ── 异步写入基础设施 ──
    private static ConcurrentQueue<string>? _queue;
    private static ManualResetEventSlim? _signal;
    private static readonly ManualResetEventSlim _flushGate = new(false);
    private static Thread? _writerThread;
    private static volatile bool _stopping;

    // ── 文件状态（仅消费者线程访问） ──
    private static StreamWriter? _writer;
    private static string? _outputDir;
    private static string _fileTimestamp = "";
    private static int _fileSequence;
    private static long _currentFileSize;

    // ── 环境上下文 ──
    private static readonly Dictionary<string, string> _context = new();
    private static readonly object _contextLock = new();

    // ── 阶段计时 ──
    private static readonly Stopwatch _phaseTimer = new();
    private static string? _currentPhase;
    private static Stopwatch? _sessionTimer;

    // ── 统计计数器（Interlocked 保证线程安全） ──
    private static int _stylesScanned;
    private static int _missingDetected;
    private static int _replacementOps;
    private static int _skippedCount;
    private static int _errorCount;

    /// <summary>诊断日志是否已启用。</summary>
    public static bool IsEnabled => _queue != null;

    // ══════════════════════════════════════════
    //  生命周期管理
    // ══════════════════════════════════════════

    /// <summary>
    /// 启用诊断日志。默认输出到插件 DLL 所在目录。
    /// 启动时自动清理超过 7 天的旧日志文件。
    /// </summary>
    [Conditional("DEBUG")]
    public static void Enable(string? outputDir = null)
    {
        if (_queue != null) return;

        outputDir ??= Path.GetDirectoryName(typeof(DiagnosticLogger).Assembly.Location)
                      ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(outputDir);
        _outputDir = outputDir;

        // 清理过期日志
        CleanupOldLogs(outputDir);

        // 初始化文件
        _fileTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _fileSequence = 0;
        _currentFileSize = 0;
        OpenNewLogFile();

        // 写入文件头（消费者线程尚未启动，直接写入安全）
        WriteHeaderDirect();

        // 启动异步消费者
        _stopping = false;
        ResetCounters();
        _sessionTimer = Stopwatch.StartNew();

        var queue = new ConcurrentQueue<string>();
        _signal = new ManualResetEventSlim(false);
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "AFR-DiagLog",
            Priority = ThreadPriority.BelowNormal
        };
        _queue = queue; // IsEnabled 变为 true — 放在最后，保证基础设施就绪
        _writerThread.Start();
    }

    /// <summary>
    /// 关闭诊断日志，排空队列并释放文件句柄。
    /// </summary>
    [Conditional("DEBUG")]
    public static void Disable()
    {
        if (_queue == null) return;

        _stopping = true;
        _signal?.Set();
        _writerThread?.Join(TimeSpan.FromSeconds(5));

        // 安全网：主线程最终排空
        DrainQueue();

        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _writerThread = null;
        _signal?.Dispose();
        _signal = null;
        _queue = null; // IsEnabled 变为 false
        _sessionTimer = null;
        ClearAllContextInternal();
    }

    /// <summary>
    /// 显式刷盘 — 确保已入队的日志全部写入磁盘。
    /// </summary>
    [Conditional("DEBUG")]
    public static void Flush()
    {
        if (_queue == null) return;
        _flushGate.Reset();
        _queue.Enqueue(FlushSentinel);
        _signal?.Set();
        _flushGate.Wait(TimeSpan.FromSeconds(3));
    }

    // ══════════════════════════════════════════
    //  环境上下文
    // ══════════════════════════════════════════

    /// <summary>设置环境上下文键值对，自动附加到每条日志末尾。</summary>
    [Conditional("DEBUG")]
    public static void SetContext(string key, string value)
    {
        lock (_contextLock) { _context[key] = value; }
    }

    /// <summary>清除指定上下文键。</summary>
    [Conditional("DEBUG")]
    public static void ClearContext(string key)
    {
        lock (_contextLock) { _context.Remove(key); }
    }

    /// <summary>清除所有上下文。</summary>
    [Conditional("DEBUG")]
    public static void ClearAllContext()
    {
        ClearAllContextInternal();
    }

    // ══════════════════════════════════════════
    //  核心日志 API
    // ══════════════════════════════════════════

    /// <summary>记录 INFO 级别日志。</summary>
    [Conditional("DEBUG")]
    public static void Info(string source, string message, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        EnqueueEntry(DiagLevel.Info, source, caller, message);
    }

    /// <summary>记录 WARN 级别日志。</summary>
    [Conditional("DEBUG")]
    public static void Warn(string source, string message, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        EnqueueEntry(DiagLevel.Warn, source, caller, message);
    }

    /// <summary>记录 ERROR 级别日志，附带异常详情与堆栈。</summary>
    [Conditional("DEBUG")]
    public static void Error(string source, string message, Exception? ex = null,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _errorCount);
        if (ex != null)
        {
            EnqueueEntry(DiagLevel.Error, source, caller,
                $"{message}: {ex.GetType().Name}: {ex.Message}");
            if (ex.StackTrace != null)
                Enqueue($"         {ex.StackTrace.Replace("\n", "\n         ")}");
        }
        else
        {
            EnqueueEntry(DiagLevel.Error, source, caller, message);
        }
    }

    // ══════════════════════════════════════════
    //  阶段与文档标记
    // ══════════════════════════════════════════

    /// <summary>记录文档信息和当前配置，同时设置文档上下文。</summary>
    [Conditional("DEBUG")]
    public static void BeginDocument(string docPath, string mainFont, string bigFont,
        string trueTypeFont, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        SetContext("Doc", Path.GetFileName(docPath));
        ResetCounters();
        _sessionTimer?.Restart();
        EnqueueEntry(DiagLevel.Info, "文档", caller, docPath);
        EnqueueEntry(DiagLevel.Info, "配置", caller,
            $"MainFont='{mainFont}' BigFont='{bigFont}' TrueType='{trueTypeFont}'");
    }

    /// <summary>标记阶段开始，自动开始计时。</summary>
    [Conditional("DEBUG")]
    public static void BeginPhase(string phaseName, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        _currentPhase = phaseName;
        _phaseTimer.Restart();
        EnqueueEntry(DiagLevel.Info, "阶段", caller, $"── 开始: {phaseName} ──");
    }

    /// <summary>标记阶段结束，输出耗时和可选摘要。</summary>
    [Conditional("DEBUG")]
    public static void EndPhase(string? summary = null, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        _phaseTimer.Stop();
        var msg = $"── 结束: {_currentPhase} (耗时: {_phaseTimer.ElapsedMilliseconds}ms)";
        if (summary != null) msg += $" {summary}";
        msg += " ──";
        EnqueueEntry(DiagLevel.Info, "阶段", caller, msg);
        _currentPhase = null;
    }

    // ══════════════════════════════════════════
    //  检测阶段
    // ══════════════════════════════════════════

    /// <summary>记录单个样式的扫描信息。</summary>
    [Conditional("DEBUG")]
    public static void LogStyleScan(string styleName, string fileName, string bigFont,
        string typeFace, bool isTrueType, bool isShapeFile,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _stylesScanned);
        EnqueueEntry(DiagLevel.Info, "样式扫描", caller,
            $"'{styleName}' FileName='{fileName}' BigFont='{bigFont}' " +
            $"TypeFace='{typeFace}' IsTrueType={isTrueType} IsShapeFile={isShapeFile}");
    }

    /// <summary>记录字体可用性检查结果。</summary>
    [Conditional("DEBUG")]
    public static void LogFontAvailability(string fontName, string checkType, bool available,
        string? detail = null, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        var msg = $"'{fontName}' 类型={checkType} 可用={available}";
        if (detail != null) msg += $" {detail}";
        EnqueueEntry(DiagLevel.Info, "可用性", caller, msg);
    }

    /// <summary>记录检测到的缺失字体。</summary>
    [Conditional("DEBUG")]
    public static void LogMissing(string styleName, bool isMainMissing, bool isBigMissing,
        bool isTrueType, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _missingDetected);
        EnqueueEntry(DiagLevel.Info, "缺失", caller,
            $"'{styleName}' 主字体缺失={isMainMissing} 大字体缺失={isBigMissing} IsTrueType={isTrueType}");
    }

    // ══════════════════════════════════════════
    //  替换阶段
    // ══════════════════════════════════════════

    /// <summary>记录替换字体预校验结果。</summary>
    [Conditional("DEBUG")]
    public static void LogPreValidation(string fontName, string role, bool valid,
        string? reason = null, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        var level = valid ? DiagLevel.Info : DiagLevel.Warn;
        var msg = $"{role}='{fontName}' 有效={valid}";
        if (reason != null) msg += $" 原因={reason}";
        EnqueueEntry(level, "预校验", caller, msg);
    }

    /// <summary>记录字体名归一化结果。</summary>
    [Conditional("DEBUG")]
    public static void LogNormalize(string input, string output,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        EnqueueEntry(DiagLevel.Info, "归一化", caller, $"'{input}' → '{output}'");
    }

    /// <summary>记录单个属性的替换操作（旧值→新值）。</summary>
    [Conditional("DEBUG")]
    public static void LogReplacement(string styleName, string property, string oldValue,
        string newValue, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _replacementOps);
        EnqueueEntry(DiagLevel.Info, "替换", caller,
            $"'{styleName}' {property}: '{oldValue}' → '{newValue}'");
    }

    /// <summary>记录跳过的样式及原因。</summary>
    [Conditional("DEBUG")]
    public static void LogSkipped(string styleName, string reason,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _skippedCount);
        EnqueueEntry(DiagLevel.Warn, "跳过", caller, $"'{styleName}' 原因={reason}");
    }

    /// <summary>记录替换后的读回验证。</summary>
    [Conditional("DEBUG")]
    public static void LogValidation(string styleName, string property, string expected,
        string actual, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        bool match = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        var level = match ? DiagLevel.Info : DiagLevel.Warn;
        var symbol = match ? "✓" : "✗";
        EnqueueEntry(level, "验证", caller,
            $"'{styleName}' {property} 期望='{expected}' 实际='{actual}' {symbol}");
    }

    // ══════════════════════════════════════════
    //  通用日志
    // ══════════════════════════════════════════

    /// <summary>记录通用分类日志（INFO 级别）。</summary>
    [Conditional("DEBUG")]
    public static void Log(string category, string message,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        EnqueueEntry(DiagLevel.Info, category, caller, message);
    }

    /// <summary>记录错误及异常详情（ERROR 级别，含完整堆栈）。</summary>
    [Conditional("DEBUG")]
    public static void LogError(string context, Exception ex,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _errorCount);
        EnqueueEntry(DiagLevel.Error, context, caller,
            $"{ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace != null)
            Enqueue($"         {ex.StackTrace.Replace("\n", "\n         ")}");
    }

    /// <summary>输出最终统计汇总。</summary>
    [Conditional("DEBUG")]
    public static void WriteSummary([CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        _sessionTimer?.Stop();
        Enqueue("");
        Enqueue("==================== 汇总 ====================");
        Enqueue($"  扫描样式数: {_stylesScanned}");
        Enqueue($"  检测缺失:   {_missingDetected}");
        Enqueue($"  替换操作:   {_replacementOps}");
        Enqueue($"  跳过:       {_skippedCount}");
        Enqueue($"  错误:       {_errorCount}");
        Enqueue($"  总耗时:     {_sessionTimer?.ElapsedMilliseconds ?? 0}ms");
        Enqueue("===============================================");
        Enqueue("");
    }

    // ══════════════════════════════════════════
    //  内部方法 — 格式化与入队
    // ══════════════════════════════════════════

    private static void EnqueueEntry(DiagLevel level, string source, string caller, string message)
    {
        string levelTag = level switch
        {
            DiagLevel.Info  => "INFO ",
            DiagLevel.Warn  => "WARN ",
            DiagLevel.Error => "ERROR",
            _               => "?    "
        };
        int tid = Environment.CurrentManagedThreadId;
        string location = string.IsNullOrEmpty(source) ? caller : $"{source}.{caller}";
        string ctx = BuildContextSuffix();
        Enqueue($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{levelTag}] [T:{tid:D2}] [{location}] {message}{ctx}");
    }

    private static void Enqueue(string line)
    {
        _queue?.Enqueue(line);
        _signal?.Set();
    }

    private static string BuildContextSuffix()
    {
        lock (_contextLock)
        {
            if (_context.Count == 0) return "";
            var sb = new StringBuilder(" |");
            foreach (var (key, value) in _context)
                sb.Append($" {key}={value}");
            return sb.ToString();
        }
    }

    // ══════════════════════════════════════════
    //  内部方法 — 消费者线程
    // ══════════════════════════════════════════

    private static void WriterLoop()
    {
        try
        {
            while (!_stopping)
            {
                _signal?.Wait(500);
                _signal?.Reset();
                DrainQueue();
            }
            DrainQueue(); // 退出前最终排空
        }
        catch { /* 消费者线程不抛异常 */ }
    }

    private static void DrainQueue()
    {
        while (_queue != null && _queue.TryDequeue(out var line))
        {
            if (ReferenceEquals(line, FlushSentinel))
            {
                try { _writer?.Flush(); } catch { }
                _flushGate.Set();
                continue;
            }
            WriteLineToFile(line);
        }
        try { _writer?.Flush(); } catch { }
    }

    private static void WriteLineToFile(string line)
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteLine(line);
            _currentFileSize += Encoding.UTF8.GetByteCount(line) + 2; // +2 for \r\n
            if (_currentFileSize >= MaxFileSize)
                RollFile();
        }
        catch { /* 写入失败静默丢弃 */ }
    }

    // ══════════════════════════════════════════
    //  内部方法 — 文件管理
    // ══════════════════════════════════════════

    private static void OpenNewLogFile()
    {
        var fileName = BuildFileName();
        var filePath = Path.Combine(_outputDir!, fileName);
        _writer = new StreamWriter(filePath, false, Encoding.UTF8) { AutoFlush = false };
        _currentFileSize = 0;
    }

    private static string BuildFileName()
    {
        return _fileSequence == 0
            ? $"{FilePrefix}{_fileTimestamp}.log"
            : $"{FilePrefix}{_fileTimestamp}_{_fileSequence:D3}.log";
    }

    private static void RollFile()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _fileSequence++;
        OpenNewLogFile();
        _writer?.WriteLine($"── 续前文件 (分包 #{_fileSequence}) ──");
        _writer?.WriteLine("");
    }

    private static void WriteHeaderDirect()
    {
        if (_writer == null) return;
        var asm = typeof(DiagnosticLogger).Assembly;
        _writer.WriteLine("================== AFR 诊断日志 ==================");
        _writer.WriteLine($"时间:     {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        _writer.WriteLine($"日志路径: {Path.Combine(_outputDir!, BuildFileName())}");
        _writer.WriteLine($"插件版本: {asm.GetName().Version}");
        _writer.WriteLine($"运行时:   {Environment.Version}");
        _writer.WriteLine($"操作系统: {Environment.OSVersion}");
        _writer.WriteLine("==================================================");
        _writer.WriteLine("");
        _writer.Flush();
        _currentFileSize += 500; // 头部大小估算
    }

    private static void CleanupOldLogs(string dir)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(dir, FilePattern))
            {
                try
                {
                    if (File.GetCreationTime(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ResetCounters()
    {
        _stylesScanned = 0;
        _missingDetected = 0;
        _replacementOps = 0;
        _skippedCount = 0;
        _errorCount = 0;
    }

    private static void ClearAllContextInternal()
    {
        lock (_contextLock) { _context.Clear(); }
    }
}

#else

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AFR.Services;

/// <summary>
/// Release 构建的空壳 — 所有方法通过 [Conditional("DEBUG")] 在编译时自动移除调用。
/// </summary>
internal static class DiagnosticLogger
{
    public static bool IsEnabled => false;

    [Conditional("DEBUG")] public static void Enable(string? outputDir = null) { }
    [Conditional("DEBUG")] public static void Disable() { }
    [Conditional("DEBUG")] public static void Flush() { }
    [Conditional("DEBUG")] public static void SetContext(string key, string value) { }
    [Conditional("DEBUG")] public static void ClearContext(string key) { }
    [Conditional("DEBUG")] public static void ClearAllContext() { }
    [Conditional("DEBUG")] public static void Info(string source, string message, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void Warn(string source, string message, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void Error(string source, string message, Exception? ex = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void BeginDocument(string docPath, string mainFont, string bigFont, string trueTypeFont, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void BeginPhase(string phaseName, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void EndPhase(string? summary = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogStyleScan(string styleName, string fileName, string bigFont, string typeFace, bool isTrueType, bool isShapeFile, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogFontAvailability(string fontName, string checkType, bool available, string? detail = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogMissing(string styleName, bool isMainMissing, bool isBigMissing, bool isTrueType, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogPreValidation(string fontName, string role, bool valid, string? reason = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogNormalize(string input, string output, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogReplacement(string styleName, string property, string oldValue, string newValue, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogSkipped(string styleName, string reason, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogValidation(string styleName, string property, string expected, string actual, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void Log(string category, string message, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogError(string context, Exception ex, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void WriteSummary([CallerMemberName] string caller = "") { }
}

#endif
