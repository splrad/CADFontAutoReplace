using Autodesk.AutoCAD.ApplicationServices;
using AFR.FontMapping;
using AFR.Models;
using AFR.Services;

namespace AFR.Hosting;

/// <summary>
/// 文档级执行状态和结果缓存。
/// <para>
/// 防止同一图纸重复执行，并为 AFRLOG 保存原始检测、二次检测和运行时映射结果。
/// </para>
/// </summary>
internal sealed class DocumentContextManager
{
    private static readonly Lazy<DocumentContextManager> _instance = new(() => new DocumentContextManager());
    /// <summary>全局文档上下文缓存。</summary>
    public static DocumentContextManager Instance => _instance.Value;

    // Key：已保存图纸用 Database.Filename，未保存图纸用 Document.Name。
    private readonly Dictionary<string, DateTime> _executedDocuments = new(StringComparer.OrdinalIgnoreCase);
    // 替换前的原始缺失结果。
    private readonly Dictionary<string, List<FontCheckResult>> _detectionResults = new(StringComparer.OrdinalIgnoreCase);
    // 替换后二次检测仍缺失的结果。
    private readonly Dictionary<string, List<FontCheckResult>> _stillMissingResults = new(StringComparer.OrdinalIgnoreCase);
    // 文件级 Hook 真实记录的运行时映射。
    private readonly Dictionary<string, List<RuntimeFontMappingResultRecord>> _runtimeFontMappingResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private DocumentContextManager() { }

    /// <summary>检查文档是否已处理；无效文档按已处理返回。</summary>
    public bool HasExecuted(Document doc)
    {
        if (doc == null) return true;
        var key = GetDocumentKey(doc);
        if (key == null) return true;
        lock (_lock)
        {
            return _executedDocuments.ContainsKey(key);
        }
    }

    /// <summary>标记文档已完成自动处理。</summary>
    public void MarkExecuted(Document doc)
    {
        if (doc == null) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        lock (_lock)
        {
            _executedDocuments[key] = DateTime.Now;
        }
    }

    /// <summary>移除文档跟踪数据，通常在文档关闭时调用。</summary>
    public void Remove(Document doc)
    {
        if (doc == null) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        IntPtr dbScope = IntPtr.Zero;
        try { dbScope = LdFileHook.GetDatabaseScope(doc.Database); } catch { }
        lock (_lock)
        {
            _executedDocuments.Remove(key);
            _detectionResults.Remove(key);
            _stillMissingResults.Remove(key);
            _runtimeFontMappingResults.Remove(key);
        }
        LdFileHook.ClearRegisteredRedirectsForDocument(dbScope);
        DiagnosticLogger.Ok(
            "DocumentContextManager",
            "Remove",
            "文档跟踪数据已移除",
            new Dictionary<string, object?>
            {
                ["documentKey"] = key,
                ["documentName"] = ReadDocumentName(doc),
                ["database"] = ReadDatabaseFilename(doc),
                ["dbScope"] = dbScope == IntPtr.Zero ? "0x0" : $"0x{dbScope.ToInt64():X}"
            });
        LogService.Instance.ResetHeaderForDocument(key);
    }

    /// <summary>清空所有文档跟踪数据，通常在插件卸载时调用。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _executedDocuments.Clear();
            _detectionResults.Clear();
            _stillMissingResults.Clear();
            _runtimeFontMappingResults.Clear();
        }
        LdFileHook.ClearRegisteredRedirects();
    }

    /// <summary>存储文档的原始缺失字体检测结果。</summary>
    public void StoreDetectionResults(Document doc, List<FontCheckResult> results)
    {
        if (doc == null) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        lock (_lock)
        {
            _detectionResults[key] = results;
        }
    }

    /// <summary>获取文档原始缺失字体检测结果。</summary>
    public List<FontCheckResult>? GetDetectionResults(Document doc)
    {
        if (doc == null) return null;
        var key = GetDocumentKey(doc);
        if (key == null) return null;
        lock (_lock)
        {
            return _detectionResults.TryGetValue(key, out var r) ? r : null;
        }
    }

    /// <summary>存储替换后仍缺失的检测结果。</summary>
    public void StoreStillMissingResults(Document doc, List<FontCheckResult> results)
    {
        if (doc == null) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        lock (_lock)
        {
            _stillMissingResults[key] = results;
        }
    }

    /// <summary>获取替换后仍缺失的检测结果。</summary>
    public List<FontCheckResult>? GetStillMissingResults(Document doc)
    {
        if (doc == null) return null;
        var key = GetDocumentKey(doc);
        if (key == null) return null;
        lock (_lock)
        {
            return _stillMissingResults.TryGetValue(key, out var r) ? r : null;
        }
    }

    /// <summary>存储文档的运行时字体映射记录。</summary>
    public void StoreRuntimeFontMappingResults(Document doc, List<RuntimeFontMappingResultRecord> results)
    {
        if (doc == null) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        lock (_lock)
        {
            _runtimeFontMappingResults[key] = results;
        }
    }

    /// <summary>获取文档的运行时字体映射记录。</summary>
    public List<RuntimeFontMappingResultRecord>? GetRuntimeFontMappingResults(Document doc)
    {
        if (doc == null) return null;
        var key = GetDocumentKey(doc);
        if (key == null) return null;
        lock (_lock)
        {
            return _runtimeFontMappingResults.TryGetValue(key, out var r) ? r : null;
        }
    }

    /// <summary>
    /// 获取文档唯一标识；已保存图纸用 Database.Filename，未保存图纸回退到 Document.Name。
    /// </summary>
    internal static string? GetDocumentKey(Document doc)
    {
        try
        {
            if (doc.IsDisposed) return null;
            var dbFilename = doc.Database?.Filename;
            return string.IsNullOrEmpty(dbFilename) ? doc.Name : dbFilename;
        }
        catch
        {
            return null;
        }
    }

    internal static string ReadDocumentName(Document doc)
    {
        try
        {
            return doc.IsDisposed ? "<disposed>" : doc.Name;
        }
        catch
        {
            return "<unavailable>";
        }
    }

    internal static string ReadDatabaseFilename(Document doc)
    {
        try
        {
            if (doc.IsDisposed) return "<disposed>";
            return doc.Database?.Filename ?? string.Empty;
        }
        catch
        {
            return "<unavailable>";
        }
    }
}
