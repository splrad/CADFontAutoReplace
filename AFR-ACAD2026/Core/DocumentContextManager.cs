using Autodesk.AutoCAD.ApplicationServices;

namespace AFR_ACAD2026.Core;

/// <summary>
/// 跟踪已处理的文档，防止重复执行。
/// 线程安全的单例模式，支持文档关闭时清理。
/// </summary>
internal sealed class DocumentContextManager
{
    private static readonly Lazy<DocumentContextManager> _instance = new(() => new DocumentContextManager());
    public static DocumentContextManager Instance => _instance.Value;

    private readonly HashSet<string> _executedDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private DocumentContextManager() { }

    public bool HasExecuted(Document doc)
    {
        if (doc == null) return true;
        lock (_lock)
        {
            return _executedDocuments.Contains(GetDocumentKey(doc));
        }
    }

    public void MarkExecuted(Document doc)
    {
        if (doc == null) return;
        lock (_lock)
        {
            _executedDocuments.Add(GetDocumentKey(doc));
        }
    }

    public void Remove(Document doc)
    {
        if (doc == null) return;
        lock (_lock)
        {
            _executedDocuments.Remove(GetDocumentKey(doc));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _executedDocuments.Clear();
        }
    }

    private static string GetDocumentKey(Document doc)
    {
        return doc.Name;
    }
}
