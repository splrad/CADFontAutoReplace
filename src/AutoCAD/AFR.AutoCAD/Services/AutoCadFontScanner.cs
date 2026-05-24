using System.IO;
using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR.Services;

/// <summary>
/// AutoCAD 平台的 <see cref="IFontScanner"/> 实现。
/// <para>
/// 通过 <see cref="CadEnvironmentSettings.GetAllFontSearchPaths"/> 获取 CAD 字体目录，
/// 扫描可用的 SHX 字体；系统已安装 TrueType 字族由 HookTrueTypeFontIndex 通过 DirectWrite 枚举。
/// SHX 扫描时同步填充 <see cref="FontManager.FontCache"/>（大字体/常规字体分类）。
/// 使用会话级缓存 — 字体列表在 CAD 运行期间不变，只扫描一次。
/// </para>
/// </summary>
internal sealed class AutoCadFontScanner : IFontScanner
{
    // 会话级缓存：首次扫描后结果不再变化
    private static SortedSet<string>? _cachedShxFonts;
    private static SortedSet<string>? _cachedTrueTypeFonts;

    /// <summary>
    /// 扫描所有字体搜索路径下的可用 SHX 字体文件名。
    /// 扫描过程中同步调用 <see cref="ShxFontAnalyzer.IsBigFont"/> 分类，
    /// 并将结果写入 <see cref="FontManager.FontCache"/>。
    /// </summary>
    public IReadOnlyCollection<string> ScanAvailableShxFonts()
    {
        if (_cachedShxFonts != null) return _cachedShxFonts;

        var fonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in CadEnvironmentSettings.GetAllFontSearchPaths())
            ScanShxDirectory(dir, fonts);

        _cachedShxFonts = fonts;
        return fonts;
    }

    /// <summary>
    /// 扫描系统已安装的 TrueType 字体，返回 DirectWrite 枚举到的字体族名和本地化名称。
    /// </summary>
    public IReadOnlyCollection<string> ScanSystemTrueTypeFonts()
    {
        if (_cachedTrueTypeFonts != null) return _cachedTrueTypeFonts;

        var fonts = new SortedSet<string>(
            HookTrueTypeFontIndex.GetAvailableFontNamesSnapshot(),
            StringComparer.OrdinalIgnoreCase);
        _cachedTrueTypeFonts = fonts;
        return fonts;
    }

    /// <summary>
    /// 扫描指定目录中的 SHX 字体文件，将文件名加入结果集合，
    /// 同时通过 <see cref="ShxFontAnalyzer.IsBigFont"/> 分类并写入 <see cref="FontManager.FontCache"/>。
    /// </summary>
    private static void ScanShxDirectory(string directory, SortedSet<string> results)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.shx"))
            {
                string fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName)) continue;

                results.Add(fileName);

                // 填充 FontCache：跳过已分类的（不同目录可能包含同名文件）
                if (!FontManager.FontCache.ContainsKey(fileName))
                {
                    bool? isBigFont = ShxFontAnalyzer.IsBigFont(file);
                    if (isBigFont.HasValue)
                        FontManager.FontCache.TryAdd(fileName, isBigFont.Value);
                }
            }
        }
        catch { }
    }
}
