using System.Collections.Concurrent;
using System.IO;

namespace AFR.Services;

/// <summary>
/// SHX 字体类型全局缓存管理器。
/// <para>
/// 维护一个进程级的 <see cref="FontCache"/> 字典，记录每个 SHX 文件名是否为大字体。
/// 提供两种填充方式：
/// <list type="bullet">
///   <item><see cref="InitializeFontCacheAsync"/> — 后台预热，插件启动时扫描所有字体目录。</item>
///   <item><see cref="UpdateCacheIncrementally"/> — 增量更新，用户触发命令前仅处理新增/删除的文件。</item>
/// </list>
/// 作为全局唯一的 SHX 类型缓存源，替代各模块中分散的局部缓存。
/// </para>
/// </summary>
public static class FontManager
{
    /// <summary>
    /// 全局 SHX 字体类型缓存。
    /// Key: 文件名（不含路径，如 "hztxt.shx"），忽略大小写。
    /// Value: true = 大字体，false = 常规字体。
    /// </summary>
    public static ConcurrentDictionary<string, bool> FontCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 后台异步预热字体缓存。扫描所有指定目录下的 .shx 文件，
    /// 调用 <see cref="ShxFontAnalyzer.IsBigFont"/> 判断类型并写入缓存。
    /// <para>
    /// 适用于插件启动时调用，在后台完成扫描，不阻塞 CAD 主线程。
    /// 使用 fire-and-forget 模式，内部已捕获所有异常，不会导致 UnobservedTaskException。
    /// </para>
    /// </summary>
    /// <param name="fontDirectories">要扫描的字体目录列表。</param>
    public static void InitializeFontCacheAsync(IEnumerable<string> fontDirectories)
    {
        // 快照目录列表，避免延迟枚举在后台线程中失效
        var directories = fontDirectories.ToArray();

        Task.Run(() =>
        {
            try
            {
                var shxFiles = new List<(string FileName, string FilePath)>();

                foreach (var dir in directories)
                {
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(dir, "*.shx"))
                        {
                            string fileName = Path.GetFileName(file);
                            // 跳过已在缓存中的文件（不同目录可能包含同名文件）
                            if (!FontCache.ContainsKey(fileName))
                                shxFiles.Add((fileName, file));
                        }
                    }
                    catch { /* 目录不可访问，跳过 */ }
                }

                Parallel.ForEach(shxFiles,
                    new ParallelOptions { MaxDegreeOfParallelism = 4 },
                    item =>
                    {
                        bool? result = ShxFontAnalyzer.IsBigFont(item.FilePath);
                        if (result.HasValue)
                            FontCache.TryAdd(item.FileName, result.Value);
                    });
            }
            catch { /* 预热失败不影响插件功能，后续按需检测 */ }
        });
    }

    /// <summary>
    /// 增量比对更新缓存。扫描所有指定目录下的 .shx 文件，
    /// 仅对新增文件调用 <see cref="ShxFontAnalyzer.IsBigFont"/>，
    /// 同时移除缓存中已不存在于任何目录中的条目。
    /// <para>
    /// 适用于用户触发 AFR / AFRLOG 命令前调用，快速同步缓存与文件系统状态。
    /// 在调用线程同步执行，通常耗时极短（仅处理差量）。
    /// </para>
    /// </summary>
    /// <param name="fontDirectories">要扫描的字体目录列表。</param>
    public static void UpdateCacheIncrementally(IEnumerable<string> fontDirectories)
    {
        // 收集所有目录下的 SHX 文件名及路径（首次出现优先）
        var currentFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in fontDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.shx"))
                {
                    string fileName = Path.GetFileName(file);
                    currentFiles.TryAdd(fileName, file);
                }
            }
            catch { /* 目录不可访问，跳过 */ }
        }

        // 新增：扫描文件系统有但缓存中没有的文件
        foreach (var (fileName, filePath) in currentFiles)
        {
            if (!FontCache.ContainsKey(fileName))
            {
                bool? result = ShxFontAnalyzer.IsBigFont(filePath);
                if (result.HasValue)
                    FontCache.TryAdd(fileName, result.Value);
            }
        }

        // 清理：移除缓存中有但文件系统已不存在的条目
        foreach (var cachedKey in FontCache.Keys)
        {
            if (!currentFiles.ContainsKey(cachedKey))
                FontCache.TryRemove(cachedKey, out _);
        }
    }
}
