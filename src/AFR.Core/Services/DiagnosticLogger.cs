#if DEBUG

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace AFR.Services;

/// <summary>日志等级。</summary>
internal enum DiagLevel { Info, Warn, Error }

/// <summary>
/// 全场景内部追踪系统 - 以 JSONL 记录插件全生命周期的时序诊断事件。
/// 仅在 DEBUG 编译模式下生效，Release 版本中常规日志调用被编译器自动移除。
///
/// 格式:  每行一个 JSON 对象，包含 seq/timestamp/level/status/module/operation/message/context/durationMs/error。
/// 输出:  插件目录下的 AFR_Diag_*.jsonl 文件（单文件上限 10MB 自动分包，保留 7 天）。
/// 启用:  在 PluginEntryBase.Initialize() 中调用 DiagnosticLogger.Enable()。
/// </summary>
internal static class DiagnosticLogger
{
    // ── 常量 ──
    private const long MaxFileSize = 10L * 1024 * 1024; // 10MB
    private const int RetentionDays = 7;
    private const string FilePrefix = "AFR_Diag_";
    private const string FileExtension = ".jsonl";
    private const string FilePattern = "AFR_Diag_*.jsonl";
    private const string StatusStart = "START";
    private const string StatusOk = "OK";
    private const string StatusFail = "FAIL";
    private const string StatusSkip = "SKIP";
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
    private static readonly Dictionary<string, string> _context = [];
#if NET9_0_OR_GREATER
    private static readonly System.Threading.Lock _contextLock = new();
#else
    private static readonly object _contextLock = new();
#endif

    private static long _sequence;

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

        CleanupOldLogs(outputDir);

        _fileTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        _fileSequence = 0;
        _currentFileSize = 0;
        Interlocked.Exchange(ref _sequence, 0);
        OpenNewLogFile();

        WriteHeaderDirect();

        _stopping = false;
        var queue = new ConcurrentQueue<string>();
        _signal = new ManualResetEventSlim(false);
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "AFR-DiagLog",
            Priority = ThreadPriority.BelowNormal
        };
        _queue = queue; // IsEnabled 变为 true - 放在最后，保证基础设施就绪
        _writerThread.Start();
    }

    /// <summary>
    /// 关闭诊断日志，排空队列并释放文件句柄。
    /// </summary>
    [Conditional("DEBUG")]
    public static void Disable()
    {
        if (_queue == null) return;

        Ok("DiagnosticLogger", "Disable", "诊断日志关闭");
        _stopping = true;
        _signal?.Set();
        _writerThread?.Join(TimeSpan.FromSeconds(5));

        DrainQueue();

        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _writerThread = null;
        _signal?.Dispose();
        _signal = null;
        _queue = null; // IsEnabled 变为 false
        ClearAllContextInternal();
    }

    /// <summary>
    /// 显式刷盘 - 确保已入队的日志全部写入磁盘。
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

    /// <summary>设置环境上下文键值对，自动写入到每条日志的 context 对象。</summary>
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
    //  结构化日志 API
    // ══════════════════════════════════════════

    [Conditional("DEBUG")]
    public static void Start(
        string module,
        string operation,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        EnqueueEntry(DiagLevel.Info, StatusStart, module, operation, message, fields, null, null);
    }

    [Conditional("DEBUG")]
    public static void Ok(
        string module,
        string operation,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null,
        long? durationMs = null,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        EnqueueEntry(DiagLevel.Info, StatusOk, module, operation, message, fields, durationMs, null);
    }

    [Conditional("DEBUG")]
    public static void Fail(
        string module,
        string operation,
        string message,
        Exception? ex = null,
        IReadOnlyDictionary<string, object?>? fields = null,
        long? durationMs = null,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        EnqueueEntry(DiagLevel.Error, StatusFail, module, operation, message, fields, durationMs, ex);
    }

    [Conditional("DEBUG")]
    public static void Skip(
        string module,
        string operation,
        string reason,
        IReadOnlyDictionary<string, object?>? fields = null,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        EnqueueEntry(DiagLevel.Warn, StatusSkip, module, operation, reason, fields, null, null);
    }

    /// <summary>
    /// 执行一个带时序日志的步骤。Release 分支也会执行 action，但不产生诊断输出。
    /// </summary>
    public static void RunStep(
        string module,
        string operation,
        string startMessage,
        string okMessage,
        Action action,
        IReadOnlyDictionary<string, object?>? fields = null,
        [CallerMemberName] string caller = "")
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(action);
#else
        if (action == null) throw new ArgumentNullException(nameof(action));
#endif

        var timer = Stopwatch.StartNew();
        Start(module, operation, startMessage, fields, caller);
        try
        {
            action();
            timer.Stop();
            Ok(module, operation, okMessage, fields, timer.ElapsedMilliseconds, caller);
        }
        catch (Exception ex)
        {
            timer.Stop();
            Fail(module, operation, okMessage, ex, fields, timer.ElapsedMilliseconds, caller);
            throw;
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
        SetContext("doc", Path.GetFileName(docPath));
        Start(
            "Document",
            "Execute",
            "文档处理开始",
            new Dictionary<string, object?>
            {
                ["path"] = docPath,
                ["mainFont"] = mainFont,
                ["bigFont"] = bigFont,
                ["trueTypeFont"] = trueTypeFont,
                ["caller"] = caller
            });
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
        Ok(
            "StyleScan",
            caller,
            "样式扫描完成",
            new Dictionary<string, object?>
            {
                ["styleName"] = styleName,
                ["fileName"] = fileName,
                ["bigFont"] = bigFont,
                ["typeFace"] = typeFace,
                ["isTrueType"] = isTrueType,
                ["isShapeFile"] = isShapeFile
            });
    }

    /// <summary>记录字体可用性检查结果。</summary>
    [Conditional("DEBUG")]
    public static void LogFontAvailability(string fontName, string checkType, bool available,
        string? detail = null, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        var fields = new Dictionary<string, object?>
        {
            ["fontName"] = fontName,
            ["checkType"] = checkType,
            ["available"] = available
        };
        if (detail != null) fields["detail"] = detail;

        EnqueueEntry(
            available ? DiagLevel.Info : DiagLevel.Warn,
            available ? StatusOk : StatusFail,
            "FontAvailability",
            caller,
            available ? "字体可用性检查通过" : "字体可用性检查未通过",
            fields,
            null,
            null);
    }

    /// <summary>记录检测到的缺失字体。</summary>
    [Conditional("DEBUG")]
    public static void LogMissing(string styleName, bool isMainMissing, bool isBigMissing,
        bool isTrueType, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Ok(
            "MissingFont",
            caller,
            "检测到缺失字体",
            new Dictionary<string, object?>
            {
                ["styleName"] = styleName,
                ["isMainMissing"] = isMainMissing,
                ["isBigMissing"] = isBigMissing,
                ["isTrueType"] = isTrueType
            });
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
        var fields = new Dictionary<string, object?>
        {
            ["fontName"] = fontName,
            ["role"] = role,
            ["valid"] = valid
        };
        if (reason != null) fields["reason"] = reason;

        EnqueueEntry(
            valid ? DiagLevel.Info : DiagLevel.Warn,
            valid ? StatusOk : StatusFail,
            "PreValidation",
            caller,
            valid ? "替换字体预校验通过" : "替换字体预校验未通过",
            fields,
            null,
            null);
    }

    /// <summary>记录字体名归一化结果。</summary>
    [Conditional("DEBUG")]
    public static void LogNormalize(string input, string output,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Ok(
            "Normalize",
            caller,
            "字体名归一化完成",
            new Dictionary<string, object?>
            {
                ["input"] = input,
                ["output"] = output
            });
    }

    /// <summary>记录单个属性的替换操作（旧值到新值）。</summary>
    [Conditional("DEBUG")]
    public static void LogReplacement(string styleName, string property, string oldValue,
        string newValue, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Ok(
            "Replacement",
            caller,
            "样式属性替换完成",
            new Dictionary<string, object?>
            {
                ["styleName"] = styleName,
                ["property"] = property,
                ["oldValue"] = oldValue,
                ["newValue"] = newValue
            });
    }

    /// <summary>记录跳过的样式及原因。</summary>
    [Conditional("DEBUG")]
    public static void LogSkipped(string styleName, string reason,
        [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        Skip(
            "StyleReplacement",
            caller,
            reason,
            new Dictionary<string, object?> { ["styleName"] = styleName });
    }

    /// <summary>记录替换后的读回验证。</summary>
    [Conditional("DEBUG")]
    public static void LogValidation(string styleName, string property, string expected,
        string actual, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled) return;
        bool match = string.Equals(expected, actual, StringComparison.Ordinal);
        EnqueueEntry(
            match ? DiagLevel.Info : DiagLevel.Warn,
            match ? StatusOk : StatusFail,
            "Validation",
            caller,
            match ? "读回验证通过" : "读回验证未通过",
            new Dictionary<string, object?>
            {
                ["styleName"] = styleName,
                ["property"] = property,
                ["expected"] = expected,
                ["actual"] = actual
            },
            null,
            null);
    }

    // ══════════════════════════════════════════
    //  内部方法 - 格式化与入队
    // ══════════════════════════════════════════

    private static void EnqueueEntry(
        DiagLevel level,
        string status,
        string module,
        string operation,
        string message,
        IReadOnlyDictionary<string, object?>? fields,
        long? durationMs,
        Exception? ex)
    {
        Enqueue(BuildJsonLine(level, status, module, operation, message, fields, durationMs, ex));
    }

    private static void Enqueue(string line)
    {
        _queue?.Enqueue(line);
        _signal?.Set();
    }

    private static string BuildJsonLine(
        DiagLevel level,
        string status,
        string module,
        string operation,
        string message,
        IReadOnlyDictionary<string, object?>? fields,
        long? durationMs,
        Exception? ex)
    {
        long seq = Interlocked.Increment(ref _sequence);
        var sb = new StringBuilder(512);
        sb.Append('{');
        AppendProperty(sb, "seq", seq);
        sb.Append(',');
        AppendProperty(sb, "timestamp", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        sb.Append(',');
        AppendProperty(sb, "level", FormatLevel(level));
        sb.Append(',');
        AppendProperty(sb, "status", status);
        sb.Append(',');
        AppendProperty(sb, "module", NormalizeModule(module));
        sb.Append(',');
        AppendProperty(sb, "operation", NormalizeOperation(operation));
        sb.Append(',');
        AppendProperty(sb, "message", message);
        sb.Append(',');
        AppendProperty(sb, "threadId", Environment.CurrentManagedThreadId);
        sb.Append(',');
        sb.Append("\"context\":");
        AppendContextObject(sb, fields);
        sb.Append(',');
        sb.Append("\"durationMs\":");
        AppendJsonValue(sb, durationMs);
        sb.Append(',');
        sb.Append("\"error\":");
        AppendException(sb, ex);
        sb.Append('}');
        return sb.ToString();
    }

    private static string FormatLevel(DiagLevel level)
        => level switch
        {
            DiagLevel.Info => "INFO",
            DiagLevel.Warn => "WARN",
            DiagLevel.Error => "ERROR",
            _ => "INFO"
        };

    private static string NormalizeModule(string module)
        => string.IsNullOrWhiteSpace(module) ? "DiagnosticLogger" : module.Trim();

    private static string NormalizeOperation(string operation)
        => string.IsNullOrWhiteSpace(operation) ? "Log" : operation.Trim();

    private static void AppendContextObject(StringBuilder sb, IReadOnlyDictionary<string, object?>? fields)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        lock (_contextLock)
        {
            foreach (var pair in _context)
                values[pair.Key] = pair.Value;
        }

        if (fields != null)
        {
            foreach (var pair in fields)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                    values[pair.Key] = pair.Value;
            }
        }

        sb.Append('{');
        bool first = true;
        foreach (var pair in values)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendJsonString(sb, pair.Key);
            sb.Append(':');
            AppendJsonValue(sb, pair.Value);
        }
        sb.Append('}');
    }

    private static void AppendProperty(StringBuilder sb, string name, object? value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        AppendJsonValue(sb, value);
    }

    private static void AppendException(StringBuilder sb, Exception? ex)
    {
        if (ex == null)
        {
            sb.Append("null");
            return;
        }

        sb.Append('{');
        AppendProperty(sb, "type", ex.GetType().FullName ?? ex.GetType().Name);
        sb.Append(',');
        AppendProperty(sb, "message", ex.Message);
        sb.Append(',');
        AppendProperty(sb, "stackTrace", ex.StackTrace);
        if (ex.InnerException != null)
        {
            sb.Append(',');
            AppendProperty(sb, "innerType", ex.InnerException.GetType().FullName ?? ex.InnerException.GetType().Name);
            sb.Append(',');
            AppendProperty(sb, "innerMessage", ex.InnerException.Message);
        }
        sb.Append('}');
    }

    private static void AppendJsonValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string text:
                AppendJsonString(sb, text);
                break;
            case bool boolean:
                sb.Append(boolean ? "true" : "false");
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case float single:
                if (float.IsNaN(single) || float.IsInfinity(single)) sb.Append("null");
                else sb.Append(single.ToString("R", CultureInfo.InvariantCulture));
                break;
            case double number:
                if (double.IsNaN(number) || double.IsInfinity(number)) sb.Append("null");
                else sb.Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case decimal dec:
                sb.Append(dec.ToString(CultureInfo.InvariantCulture));
                break;
            case DateTime dateTime:
                AppendJsonString(sb, dateTime.ToString("o", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dateTimeOffset:
                AppendJsonString(sb, dateTimeOffset.ToString("o", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                sb.Append(timeSpan.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture));
                break;
            case Enum enumValue:
                AppendJsonString(sb, enumValue.ToString());
                break;
            default:
                AppendJsonString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static void AppendJsonString(StringBuilder sb, string? value)
    {
        sb.Append('"');
        string? text = value;
        if (!string.IsNullOrEmpty(text))
        {
            foreach (char c in text!)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
        }
        sb.Append('"');
    }

    // ══════════════════════════════════════════
    //  内部方法 - 消费者线程
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
    //  内部方法 - 文件管理
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
            ? $"{FilePrefix}{_fileTimestamp}{FileExtension}"
            : $"{FilePrefix}{_fileTimestamp}_{_fileSequence:D3}{FileExtension}";
    }

    private static void RollFile()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _fileSequence++;
        OpenNewLogFile();
        WriteLineToFile(BuildJsonLine(
            DiagLevel.Info,
            StatusOk,
            "DiagnosticLogger",
            "RollFile",
            "诊断日志分包已创建",
            new Dictionary<string, object?> { ["fileSequence"] = _fileSequence },
            null,
            null));
    }

    private static void WriteHeaderDirect()
    {
        if (_writer == null) return;
        string filePath = Path.Combine(_outputDir!, BuildFileName());
        WriteLineToFile(BuildJsonLine(
            DiagLevel.Info,
            StatusOk,
            "DiagnosticLogger",
            "Enable",
            "诊断日志已启用",
            new Dictionary<string, object?>
            {
                ["format"] = "jsonl",
                ["logPath"] = filePath,
                ["pluginVersion"] = PluginVersionService.GetPluginVersion(),
                ["runtime"] = Environment.Version.ToString(),
                ["os"] = Environment.OSVersion.ToString(),
                ["retentionDays"] = RetentionDays,
                ["maxFileSizeBytes"] = MaxFileSize
            },
            null,
            null));
        try { _writer.Flush(); } catch { }
    }

    private static void CleanupOldLogs(string dir)
    {
        CleanupOldLogsByPattern(dir, FilePattern);
    }

    private static void CleanupOldLogsByPattern(string dir, string pattern)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(dir, pattern))
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

    private static void ClearAllContextInternal()
    {
        lock (_contextLock) { _context.Clear(); }
    }
}

#else

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AFR.Services;

/// <summary>
/// Release 构建的空壳 - 所有普通日志方法通过 [Conditional("DEBUG")] 在编译时自动移除调用。
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
    [Conditional("DEBUG")] public static void Start(string module, string operation, string message, IReadOnlyDictionary<string, object?>? fields = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void Ok(string module, string operation, string message, IReadOnlyDictionary<string, object?>? fields = null, long? durationMs = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void Fail(string module, string operation, string message, Exception? ex = null, IReadOnlyDictionary<string, object?>? fields = null, long? durationMs = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void Skip(string module, string operation, string reason, IReadOnlyDictionary<string, object?>? fields = null, [CallerMemberName] string caller = "") { }
    public static void RunStep(string module, string operation, string startMessage, string okMessage, Action action, IReadOnlyDictionary<string, object?>? fields = null, [CallerMemberName] string caller = "") => action();
    [Conditional("DEBUG")] public static void BeginDocument(string docPath, string mainFont, string bigFont, string trueTypeFont, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogStyleScan(string styleName, string fileName, string bigFont, string typeFace, bool isTrueType, bool isShapeFile, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogFontAvailability(string fontName, string checkType, bool available, string? detail = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogMissing(string styleName, bool isMainMissing, bool isBigMissing, bool isTrueType, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogPreValidation(string fontName, string role, bool valid, string? reason = null, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogNormalize(string input, string output, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogReplacement(string styleName, string property, string oldValue, string newValue, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogSkipped(string styleName, string reason, [CallerMemberName] string caller = "") { }
    [Conditional("DEBUG")] public static void LogValidation(string styleName, string property, string expected, string actual, [CallerMemberName] string caller = "") { }
}

#endif
