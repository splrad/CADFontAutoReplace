using System.Diagnostics;
using System.IO;
using System.Text;

namespace AFR.Services;

/// <summary>
/// 独立诊断日志模块 — 用于调试字体检测与替换流程。
/// 输出全量明细到插件目录下的日志文件，供开发者排查问题。
/// 未调用 Enable() 时所有方法为空操作，零运行时开销。
///
/// 启用: 在 PluginEntryBase.Initialize() 中调用 DiagnosticLogger.Enable();
/// 禁用: 注释掉该行即可。
/// </summary>
internal static class DiagnosticLogger
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();
    private static readonly Stopwatch _phaseTimer = new();
    private static string? _currentPhase;
    private static Stopwatch? _sessionTimer;

    // 统计计数器
    private static int _stylesScanned;
    private static int _missingDetected;
    private static int _replacementOps;
    private static int _skippedCount;
    private static int _errorCount;

    /// <summary>诊断日志是否已启用。</summary>
    public static bool IsEnabled => _writer != null;

    /// <summary>
    /// 启用诊断日志，输出到指定目录。默认输出到插件 DLL 所在目录。
    /// </summary>
    public static void Enable(string? outputDir = null)
    {
        lock (_lock)
        {
            if (_writer != null) return;

            outputDir ??= Path.GetDirectoryName(typeof(DiagnosticLogger).Assembly.Location)
                          ?? Environment.CurrentDirectory;

            Directory.CreateDirectory(outputDir);
            var fileName = $"AFR_Diag_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            var filePath = Path.Combine(outputDir, fileName);

            _writer = new StreamWriter(filePath, false, Encoding.UTF8) { AutoFlush = true };
            _sessionTimer = Stopwatch.StartNew();
            ResetCounters();

            WriteRaw("================== AFR 诊断日志 ==================");
            WriteRaw($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            WriteRaw($"日志路径: {filePath}");
            WriteRaw("==================================================");
            WriteRaw("");
        }
    }

    /// <summary>
    /// 关闭诊断日志并释放文件句柄。
    /// </summary>
    public static void Disable()
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _sessionTimer = null;
        }
    }

    // ── 阶段标记 ──

    /// <summary>记录文档信息和当前配置。</summary>
    public static void BeginDocument(string docPath, string mainFont, string bigFont, string trueTypeFont)
    {
        if (!IsEnabled) return;
        WriteRaw($"[文档] {docPath}");
        WriteRaw($"[配置] MainFont='{mainFont}' BigFont='{bigFont}' TrueType='{trueTypeFont}'");
        WriteRaw("");
        ResetCounters();
    }

    /// <summary>标记阶段开始，自动开始计时。</summary>
    public static void BeginPhase(string phaseName)
    {
        if (!IsEnabled) return;
        _currentPhase = phaseName;
        _phaseTimer.Restart();
        WriteTimestamped($"[阶段开始] {phaseName}");
    }

    /// <summary>标记阶段结束，输出耗时和可选摘要。</summary>
    public static void EndPhase(string? summary = null)
    {
        if (!IsEnabled) return;
        _phaseTimer.Stop();
        var msg = $"[阶段结束] {_currentPhase} (耗时: {_phaseTimer.ElapsedMilliseconds}ms)";
        if (summary != null) msg += $" {summary}";
        WriteTimestamped(msg);
        WriteRaw("");
        _currentPhase = null;
    }

    // ── 检测阶段 ──

    /// <summary>记录单个样式的扫描信息。</summary>
    public static void LogStyleScan(string styleName, string fileName, string bigFont,
        string typeFace, bool isTrueType, bool isShapeFile)
    {
        if (!IsEnabled) return;
        _stylesScanned++;
        WriteTimestamped($"[样式扫描] '{styleName}' FileName='{fileName}' BigFont='{bigFont}' " +
                         $"TypeFace='{typeFace}' IsTrueType={isTrueType} IsShapeFile={isShapeFile}");
    }

    /// <summary>记录字体可用性检查结果。</summary>
    public static void LogFontAvailability(string fontName, string checkType, bool available, string? detail = null)
    {
        if (!IsEnabled) return;
        var msg = $"[可用性] '{fontName}' 类型={checkType} 可用={available}";
        if (detail != null) msg += $" {detail}";
        WriteTimestamped(msg);
    }

    /// <summary>记录检测到的缺失字体。</summary>
    public static void LogMissing(string styleName, bool isMainMissing, bool isBigMissing, bool isTrueType)
    {
        if (!IsEnabled) return;
        _missingDetected++;
        WriteTimestamped($"[缺失] '{styleName}' 主字体缺失={isMainMissing} 大字体缺失={isBigMissing} IsTrueType={isTrueType}");
    }

    // ── 替换阶段 ──

    /// <summary>记录替换字体预校验结果。</summary>
    public static void LogPreValidation(string fontName, string role, bool valid, string? reason = null)
    {
        if (!IsEnabled) return;
        var msg = $"[预校验] {role}='{fontName}' 有效={valid}";
        if (reason != null) msg += $" 原因={reason}";
        WriteTimestamped(msg);
    }

    /// <summary>记录字体名归一化结果。</summary>
    public static void LogNormalize(string input, string output)
    {
        if (!IsEnabled) return;
        WriteTimestamped($"[归一化] '{input}' → '{output}'");
    }

    /// <summary>记录单个属性的替换操作（旧值→新值）。</summary>
    public static void LogReplacement(string styleName, string property, string oldValue, string newValue)
    {
        if (!IsEnabled) return;
        _replacementOps++;
        WriteTimestamped($"[替换] '{styleName}' {property}: '{oldValue}' → '{newValue}'");
    }

    /// <summary>记录跳过的样式及原因。</summary>
    public static void LogSkipped(string styleName, string reason)
    {
        if (!IsEnabled) return;
        _skippedCount++;
        WriteTimestamped($"[跳过] '{styleName}' 原因={reason}");
    }

    /// <summary>记录替换后的读回验证。</summary>
    public static void LogValidation(string styleName, string property, string expected, string actual)
    {
        if (!IsEnabled) return;
        var match = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ? "✓" : "✗";
        WriteTimestamped($"[验证] '{styleName}' {property} 期望='{expected}' 实际='{actual}' {match}");
    }

    // ── 通用 ──

    /// <summary>记录通用分类日志。</summary>
    public static void Log(string category, string message)
    {
        if (!IsEnabled) return;
        WriteTimestamped($"[{category}] {message}");
    }

    /// <summary>记录错误及异常详情。</summary>
    public static void LogError(string context, Exception ex)
    {
        if (!IsEnabled) return;
        _errorCount++;
        WriteTimestamped($"[错误] {context}: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace != null)
            WriteRaw($"         {ex.StackTrace.Replace("\n", "\n         ")}");
    }

    /// <summary>输出最终统计汇总并关闭当前文档的诊断段。</summary>
    public static void WriteSummary()
    {
        if (!IsEnabled) return;
        _sessionTimer?.Stop();
        WriteRaw("");
        WriteRaw("==================== 汇总 ====================");
        WriteRaw($"扫描样式数: {_stylesScanned}");
        WriteRaw($"检测缺失: {_missingDetected}");
        WriteRaw($"替换操作: {_replacementOps}");
        WriteRaw($"跳过: {_skippedCount}");
        WriteRaw($"错误: {_errorCount}");
        WriteRaw($"总耗时: {_sessionTimer?.ElapsedMilliseconds ?? 0}ms");
        WriteRaw("===============================================");
        WriteRaw("");
    }

    // ── 内部方法 ──

    private static void ResetCounters()
    {
        _stylesScanned = 0;
        _missingDetected = 0;
        _replacementOps = 0;
        _skippedCount = 0;
        _errorCount = 0;
    }

    private static void WriteTimestamped(string message)
    {
        lock (_lock)
        {
            _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }

    private static void WriteRaw(string message)
    {
        lock (_lock)
        {
            _writer?.WriteLine(message);
        }
    }
}
