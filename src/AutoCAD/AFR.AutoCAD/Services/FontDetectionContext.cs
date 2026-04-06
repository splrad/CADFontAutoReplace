using System.Collections.Concurrent;
using Autodesk.AutoCAD.DatabaseServices;

namespace AFR.Services;

/// <summary>
/// 单次字体检测/替换事务的执行上下文。
/// <para>
/// 封装 Database 引用和两类查询缓存（FindFile、TrueType 字体度量）。
/// SHX 类型分类已统一由全局 <see cref="FontManager.FontCache"/> 管理。
/// 生命周期与单次 Execute 事务绑定，事务结束后整个上下文及缓存由 GC 自动回收。
/// 不同图纸、不同执行次数之间实现 100% 内存隔离，避免缓存污染。
/// </para>
/// </summary>
public sealed class FontDetectionContext
{
    /// <summary>当前事务操作的 AutoCAD 数据库。</summary>
    public Database Db { get; }

    /// <summary>
    /// FindFile 结果缓存，避免对同一字体重复调用 HostApplicationServices.FindFile。
    /// Key: "{FindFileHint数值}:{归一化文件名}"，Value: 是否找到。
    /// </summary>
    public ConcurrentDictionary<string, bool> FindFileCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// TrueType 字体度量缓存，避免对同一字体重复调用 GDI API。
    /// Key: 字体名，Value: (CharacterSet, PitchAndFamily) 用于构造 FontDescriptor。
    /// </summary>
    public ConcurrentDictionary<string, (int CharacterSet, int PitchAndFamily)> FontMetricsCache { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 创建一个新的字体检测上下文。
    /// </summary>
    /// <param name="db">要操作的 AutoCAD 数据库，不能为 null。</param>
    public FontDetectionContext(Database db)
    {
        Db = db ?? throw new ArgumentNullException(nameof(db));
    }
}
