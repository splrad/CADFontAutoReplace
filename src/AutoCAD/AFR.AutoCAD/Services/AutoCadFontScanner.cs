using System.IO;
using System.Windows.Markup;
using System.Windows.Media;
using AFR.Abstractions;

namespace AFR.Services;

/// <summary>
/// AutoCAD 平台的 <see cref="IFontScanner"/> 实现。
/// <para>
/// 通过 <see cref="CadEnvironmentSettings.GetAllFontSearchPaths"/> 获取统一的搜索路径，
/// 扫描可用的 SHX 字体和系统已安装的 TrueType 字体。
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
    /// 扫描系统已安装的 TrueType 字体，返回中文本地化名称（如"宋体"）。
    /// 会过滤掉无中文名称的字体、包含非法字符的名称，以及文件名与显示名不匹配的伪名称。
    /// </summary>
    public IReadOnlyCollection<string> ScanSystemTrueTypeFonts()
    {
        if (_cachedTrueTypeFonts != null) return _cachedTrueTypeFonts;

        var fonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var zhCN = XmlLanguage.GetLanguage("zh-cn");
            var zh = XmlLanguage.GetLanguage("zh");

            foreach (var family in Fonts.SystemFontFamilies)
            {
                string? displayName = null;

                if (family.FamilyNames.TryGetValue(zhCN, out var zhCNName)
                    && !string.IsNullOrWhiteSpace(zhCNName))
                {
                    displayName = zhCNName;
                }
                else if (family.FamilyNames.TryGetValue(zh, out var zhName)
                         && !string.IsNullOrWhiteSpace(zhName))
                {
                    displayName = zhName;
                }

                if (displayName == null) continue;
                if (HasInvalidChars(displayName)) continue;
                if (!ValidateFontName(family, displayName)) continue;

                fonts.Add(displayName);
            }
        }
        catch { }
        _cachedTrueTypeFonts = fonts;
        return fonts;
    }

    /// <summary>
    /// 验证字体显示名称与实际字体文件的匹配性。
    /// 当文件名和显示名都包含 CJK 字符时，要求至少有一个共同的汉字，
    /// 防止系统返回不正确的本地化名称。
    /// </summary>
    private static bool ValidateFontName(FontFamily family, string displayName)
    {
        try
        {
            var typeface = new Typeface(family, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyph))
                return true;

            string? filePath = glyph.FontUri?.LocalPath;
            if (string.IsNullOrEmpty(filePath)) return true;

            string fileName = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrEmpty(fileName)) return true;

            if (!ContainsCjk(fileName) || !ContainsCjk(displayName))
                return true;

            foreach (char c in displayName)
            {
                if (c >= '\u4E00' && c <= '\u9FFF' && fileName.Contains(c))
                    return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>检查字符串是否包含 CJK 统一汉字（U+4E00 ~ U+9FFF）。</summary>
    private static bool ContainsCjk(string s)
    {
        foreach (char c in s)
        {
            if (c >= '\u4E00' && c <= '\u9FFF') return true;
        }
        return false;
    }

    /// <summary>
    /// 检查字体名称是否包含非法字符。
    /// 允许：字母、数字、空格、连字符、下划线、括号、点号、中间点。
    /// </summary>
    private static bool HasInvalidChars(string name)
    {
        foreach (char c in name)
        {
            if (char.IsDigit(c)) continue;
            if (char.IsLetter(c))
            {
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.ModifierLetter)
                    return true;
                continue;
            }
            if (c is ' ' or '-' or '_' or '(' or ')' or '（' or '）' or '.' or '·') continue;
            return true;
        }
        return false;
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
