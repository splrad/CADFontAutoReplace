using Autodesk.AutoCAD.ApplicationServices;
using AFR_ACAD2026.FontMapping;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.Core;

/// <summary>
/// 跟踪已处理的文档，防止重复执行。
/// 使用 Dictionary 按 Database.Filename 唯一标识每个图纸，
/// 未保存图纸使用 Document.Name 作为临时标识。
/// 线程安全的单例模式，支持文档关闭时清理。
/// </summary>
internal sealed class DocumentContextManager
{
    private static readonly Lazy<DocumentContextManager> _instance = new(() => new DocumentContextManager());
    public static DocumentContextManager Instance => _instance.Value;

    // Key: 图纸唯一标识（Database.Filename 或临时标识）
    // Value: 首次处理时间
    private readonly Dictionary<string, DateTime> _executedDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<FontCheckResult>> _detectionResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<InlineFontFixRecord>> _inlineFontFixResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private DocumentContextManager() { }

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

    public void Remove(Document doc)
    {
        if (doc == null) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        lock (_lock)
        {
            _executedDocuments.Remove(key);
            _detectionResults.Remove(key);
            _inlineFontFixResults.Remove(key);
        }
        LogService.Instance.ResetHeaderForDocument(key);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _executedDocuments.Clear();
            _detectionResults.Clear();
            _inlineFontFixResults.Clear();
        }
    }

    /// <summary>
    /// 存储文档的缺失字体检测结果，供 AFRLOG 命令使用。
    /// </summary>
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

    /// <summary>
    /// 获取文档最近一次的缺失字体检测结果。
    /// </summary>
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

    /// <summary>
    /// 存储文档的内联字体修复记录，供 AFRLOG 命令使用。
    /// </summary>
    public void StoreInlineFontFixResults(Document doc, List<InlineFontFixRecord> results)
    {
        if (doc == null || results.Count == 0) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        lock (_lock)
        {
            _inlineFontFixResults[key] = results;
        }
    }

    /// <summary>
    /// 获取文档的内联字体修复记录。
    /// </summary>
    public List<InlineFontFixRecord>? GetInlineFontFixResults(Document doc)
    {
        if (doc == null) return null;
        var key = GetDocumentKey(doc);
        if (key == null) return null;
        lock (_lock)
        {
            return _inlineFontFixResults.TryGetValue(key, out var r) ? r : null;
        }
    }

    /// <summary>
    /// 获取文档唯一标识。
    /// 优先使用 Database.Filename（已保存图纸的完整路径），
    /// 未保存图纸（Filename 为空）则回退到 Document.Name 作为临时标识。
    /// 访问已释放文档时返回 null。
    /// </summary>
    private static string? GetDocumentKey(Document doc)
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
}
