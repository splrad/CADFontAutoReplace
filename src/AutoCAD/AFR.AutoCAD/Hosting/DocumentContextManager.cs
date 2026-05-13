using Autodesk.AutoCAD.ApplicationServices;
using AFR.FontMapping;
using AFR.Models;
using AFR.Services;

namespace AFR.Hosting;

/// <summary>
/// 文档上下文管理器，跟踪已处理的文档并存储每个文档的检测/替换结果。
/// <para>
/// 核心职责：
/// <list type="bullet">
///   <item>防止对同一文档重复执行字体替换（通过 <see cref="HasExecuted"/>/<see cref="MarkExecuted"/> 控制）；</item>
///   <item>存储每个文档的缺失字体检测结果、替换后仍缺失的结果、MText 内联修复记录和运行时映射记录，供 AFRLOG 命令查询。</item>
/// </list>
/// 使用 Database.Filename 作为已保存图纸的唯一标识，未保存图纸回退到 Document.Name。
/// 线程安全的全局单例，支持文档关闭时清理。
/// </para>
/// </summary>
internal sealed class DocumentContextManager
{
    private static readonly Lazy<DocumentContextManager> _instance = new(() => new DocumentContextManager());
    /// <summary>获取 DocumentContextManager 的全局唯一实例。</summary>
    public static DocumentContextManager Instance => _instance.Value;

    // Key: 图纸唯一标识（已保存 = Database.Filename，未保存 = Document.Name）
    // Value: 首次执行完整字体替换流程的时间戳
    private readonly Dictionary<string, DateTime> _executedDocuments = new(StringComparer.OrdinalIgnoreCase);
    // 缺失字体检测结果（替换前的原始状态）
    private readonly Dictionary<string, List<FontCheckResult>> _detectionResults = new(StringComparer.OrdinalIgnoreCase);
    // 替换后仍然缺失的字体列表
    private readonly Dictionary<string, List<FontCheckResult>> _stillMissingResults = new(StringComparer.OrdinalIgnoreCase);
    // MText 内联字体修复记录
    private readonly Dictionary<string, List<InlineFontFixRecord>> _inlineFontFixResults = new(StringComparer.OrdinalIgnoreCase);
    // Hook 运行时字体映射记录
    private readonly Dictionary<string, List<RuntimeFontMappingRecord>> _runtimeFontMappingResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private DocumentContextManager() { }

    /// <summary>检查指定文档是否已执行过字体替换。doc 为 null 或已释放时返回 true（视为已处理，跳过执行）。</summary>
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

    /// <summary>标记指定文档已完成字体替换，后续不再重复执行。</summary>
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

    /// <summary>移除指定文档的所有跟踪数据（执行记录 + 检测结果），并重置日志头状态。通常在文档关闭时调用。</summary>
    public void Remove(Document doc)
    {
        if (doc == null) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        lock (_lock)
        {
            _executedDocuments.Remove(key);
            _detectionResults.Remove(key);
            _stillMissingResults.Remove(key);
            _inlineFontFixResults.Remove(key);
            _runtimeFontMappingResults.Remove(key);
        }
        LogService.Instance.ResetHeaderForDocument(key);
    }

    /// <summary>清空所有文档的跟踪数据。通常在插件卸载时调用。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _executedDocuments.Clear();
            _detectionResults.Clear();
            _stillMissingResults.Clear();
            _inlineFontFixResults.Clear();
            _runtimeFontMappingResults.Clear();
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
    /// 存储替换后仍然缺失的字体检测结果。
    /// </summary>
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

    /// <summary>
    /// 获取替换后仍然缺失的字体检测结果。
    /// </summary>
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
    /// 存储文档的运行时字体映射记录，供 AFRLOG 命令使用。
    /// </summary>
    public void StoreRuntimeFontMappingResults(Document doc, List<RuntimeFontMappingRecord> results)
    {
        if (doc == null) return;
        var key = GetDocumentKey(doc);
        if (key == null) return;
        lock (_lock)
        {
            _runtimeFontMappingResults[key] = results;
        }
    }

    /// <summary>
    /// 获取文档的运行时字体映射记录。
    /// </summary>
    public List<RuntimeFontMappingRecord>? GetRuntimeFontMappingResults(Document doc)
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
