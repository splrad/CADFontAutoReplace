using System.Collections.Concurrent;
using Autodesk.AutoCAD.DatabaseServices;

namespace AFR.Services;

/// <summary>
/// 单次字体检测/替换的执行上下文。
/// <para>
/// 只保存当前数据库引用和 TrueType 度量缓存；字体可用性由共享索引判断。
/// </para>
/// </summary>
public sealed class FontDetectionContext
{
    /// <summary>当前事务操作的 AutoCAD 数据库。</summary>
    public Database Db { get; }

    /// <summary>
    /// TrueType 字体度量缓存，避免对同一字体重复调用 GDI API。
    /// </summary>
    public ConcurrentDictionary<string, (int CharacterSet, int PitchAndFamily)> FontMetricsCache { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>创建新的字体检测上下文。</summary>
    /// <param name="db">要操作的 AutoCAD 数据库，不能为 null。</param>
    public FontDetectionContext(Database db)
    {
        Db = db ?? throw new ArgumentNullException(nameof(db));
    }
}
