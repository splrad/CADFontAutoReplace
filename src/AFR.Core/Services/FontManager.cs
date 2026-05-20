using System.Collections.Concurrent;

namespace AFR.Services;

/// <summary>
/// SHX 字体类型全局缓存管理器。
/// <para>
/// 维护一个进程级的 <see cref="FontCache"/> 字典，记录每个 SHX 文件名是否为大字体。
/// 缓存由 AutoCadFontScanner（按需懒加载）和字体可用性索引（Hook 安装时同步扫描）填充。
/// 作为全局唯一的 SHX 类型缓存源，替代各模块中分散的局部缓存。
/// </para>
/// </summary>
public static class FontManager
{
    /// <summary>
    /// 全局 SHX 字体类型缓存。
    /// Key: 文件名（不含路径，如 "hztxt.shx"），大小写敏感。
    /// Value: true = 大字体，false = 常规字体。
    /// </summary>
    public static ConcurrentDictionary<string, bool> FontCache { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取常规 SHX 主字体的排序快照（FontCache 中 Value 为 false 的 Key）。
    /// 线程安全：返回的是枚举瞬间的静态副本，不受后续写入影响。
    /// </summary>
    public static IReadOnlyList<string> GetMainFontSnapshot()
    {
        var list = new List<string>();
        foreach (var kvp in FontCache)
        {
            if (!kvp.Value) list.Add(kvp.Key);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>
    /// 获取 SHX 大字体的排序快照（FontCache 中 Value 为 true 的 Key）。
    /// 线程安全：返回的是枚举瞬间的静态副本，不受后续写入影响。
    /// </summary>
    public static IReadOnlyList<string> GetBigFontSnapshot()
    {
        var list = new List<string>();
        foreach (var kvp in FontCache)
        {
            if (kvp.Value) list.Add(kvp.Key);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
