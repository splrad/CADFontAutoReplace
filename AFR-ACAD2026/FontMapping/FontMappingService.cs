using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.FontMapping;

/// <summary>
/// 字体映射服务 — 在插件初始化阶段注入字体映射规则，
/// 解决 2004 版 DWG 图纸因字体缺失/不匹配导致的多行文字乱码问题。
///
/// 原理：
/// AutoCAD 解析 DWG 文件时，通过 SHX 字体文件确定文字的代码页编码。
/// 若字体文件缺失或与原始字体不一致，代码页转换出错，导致中文乱码。
/// 通过在文件解析前调用 acdb25.dll 的 addMapping 导出函数，
/// 将缺失/竖排字体名映射到可用字体，确保编码转换正确。
///
/// 典型场景：
/// - @gbcbig → gbcbig（竖排大字体映射到常规大字体）
/// - 缺失的自定义 SHX → 系统已有的兼容 SHX
/// </summary>
internal static class FontMappingService
{
    private static volatile bool _initialized;
    private static readonly object _lock = new();

    /// <summary>
    /// 默认字体映射规则。
    /// Key = 源字体名（图纸中引用的字体），Value = 目标字体名（本机可用的字体）。
    /// </summary>
    private static readonly Dictionary<string, string> DefaultMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // 竖排大字体 → 常规大字体
        ["@gbcbig"] = "gbcbig",
    };

    /// <summary>
    /// 在插件初始化阶段注入所有字体映射。
    /// 必须在任何文档打开之前调用（PluginEntry.Initialize 中）。
    /// 多次调用安全（仅首次执行）。
    /// </summary>
    internal static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            var log = LogService.Instance;
            int successCount = 0;

            foreach (var (source, target) in DefaultMappings)
            {
                try
                {
                    bool result = NativeFontMap.AddMapping(source, target);
                    if (result)
                    {
                        log.Info($"字体映射已添加: {source} → {target}");
                        successCount++;
                    }
                    else
                    {
                        log.Warning($"字体映射添加失败（返回 false）: {source} → {target}");
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"字体映射添加异常: {source} → {target}", ex);
                }
            }

            if (successCount > 0)
            {
                log.Info($"字体映射初始化完成，共 {successCount} 条规则生效。");
            }

            _initialized = true;
        }
    }

    /// <summary>
    /// 诊断用：查询指定字体名的当前映射结果。
    /// </summary>
    internal static string QueryMapping(string fontName)
    {
        try
        {
            return NativeFontMap.MapFontName(fontName);
        }
        catch
        {
            return fontName;
        }
    }
}
