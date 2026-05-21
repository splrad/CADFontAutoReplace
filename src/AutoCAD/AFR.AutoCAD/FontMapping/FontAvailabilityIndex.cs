using System.IO;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// Hook 侧进程级字体可用性兜底索引。
/// <para>
/// 普通样式表检测和写回以当前 Database 上的 HostApplicationServices.FindFile 为权威；
/// 此索引只服务 ldfile/AcGiTextStyle 等 native 回调附近无法安全取得托管 Database 的路径。
/// </para>
/// </summary>
internal static class FontAvailabilityIndex
{
    private static readonly object CacheLock = new();
    private static readonly HashSet<string> AvailableFonts = new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool _initialized;

    internal static void Initialize()
    {
        bool scanned = EnsureInitialized();
        DiagnosticLogger.Log("FontMapping",
            scanned
                ? "字体可用性索引已初始化。"
                : "字体可用性索引已复用。");
    }

    internal static bool IsKnownAvailableFont(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        EnsureInitialized();

        string fileName = Path.GetFileName(fontName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = fontName.Trim();

        if (AvailableFonts.Contains(fileName))
            return true;

        return fileName.Length > 1
               && fileName[0] == '@'
               && AvailableFonts.Contains(fileName.TrimStart('@'));
    }

    internal static bool IsExactKnownAvailableFont(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        EnsureInitialized();

        string fileName = Path.GetFileName(fontName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = fontName.Trim();

        return AvailableFonts.Contains(fileName);
    }

    internal static bool TryGetKnownShxFontKind(string fontName, out bool isBigFont)
    {
        isBigFont = false;
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        EnsureInitialized();

        string fileName = Path.GetFileName(fontName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = fontName.Trim();

        if (fileName.Length > 1 && fileName[0] == '@')
            fileName = fileName.TrimStart('@');

        if (!fileName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            fileName = FontRedirectResolver.EnsureShx(fileName);

        return FontManager.FontCache.TryGetValue(fileName, out isBigFont);
    }

    private static bool EnsureInitialized()
    {
        if (_initialized)
            return false;

        lock (CacheLock)
        {
            if (_initialized)
                return false;

            ScanAvailableFonts();
            _initialized = true;
            return true;
        }
    }

    private static void ScanAvailableFonts()
    {
        foreach (var dir in CadEnvironmentSettings.GetAllFontSearchPaths())
            ScanDirectory(dir);

        DiagnosticLogger.Log("FontMapping",
            $"Hook 侧字体兜底索引 {AvailableFonts.Count} 项；系统 TrueType 字族由 FontDetector 后台索引处理。");
    }

    private static void ScanDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".shx", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(file);
                    AvailableFonts.Add(fileName);
                    ClassifyShxFont(file, fileName);
                }
                else if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".otf", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(file);
                    AvailableFonts.Add(fileName);
                    string familyLikeName = Path.GetFileNameWithoutExtension(fileName);
                    if (!string.IsNullOrWhiteSpace(familyLikeName))
                        AvailableFonts.Add(familyLikeName);
                }
            }
        }
        catch
        {
            // 字体目录不可读时跳过，后续按缺失字体处理。
        }
    }

    private static void ClassifyShxFont(string filePath, string fileName)
    {
        if (FontManager.FontCache.ContainsKey(fileName)) return;
        bool? result = ShxFontAnalyzer.IsBigFont(filePath);
        if (result.HasValue)
            FontManager.FontCache.TryAdd(fileName, result.Value);
    }
}
