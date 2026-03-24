using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Markup;
using System.Windows.Media;
using AFR_ACAD2026.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.UI;

/// <summary>
/// 字体选择窗口的 ViewModel，管理 UI 状态与字体数据。
/// 不包含注册表操作 — 配置保存由命令层 (AfrCommands) 处理。
/// </summary>
internal sealed class FontSelectionViewModel : INotifyPropertyChanged
{
    // 会话级缓存 — SHX 和 TrueType 字体列表在 CAD 运行期间不变，只扫描一次
    private static SortedSet<string>? _cachedShxFonts;
    private static SortedSet<string>? _cachedTrueTypeFonts;

    private string _selectedMainFont = string.Empty;
    private string _selectedBigFont = string.Empty;
    private string _selectedTrueTypeFont = string.Empty;

    /// <summary>当前 CAD Fonts 目录下可用的 SHX 字体列表。</summary>
    public ObservableCollection<string> AvailableFonts { get; }

    /// <summary>系统已安装的 TrueType 中文字体列表。</summary>
    public ObservableCollection<string> AvailableTrueTypeFonts { get; }

    public string SelectedMainFont
    {
        get => _selectedMainFont;
        set
        {
            if (_selectedMainFont == value) return;
            _selectedMainFont = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConfirmEnabled));
        }
    }

    public string SelectedBigFont
    {
        get => _selectedBigFont;
        set
        {
            if (_selectedBigFont == value) return;
            _selectedBigFont = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public string SelectedTrueTypeFont
    {
        get => _selectedTrueTypeFont;
        set
        {
            if (_selectedTrueTypeFont == value) return;
            _selectedTrueTypeFont = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    /// <summary>主字体已选择时启用确认按钮。</summary>
    public bool IsConfirmEnabled => !string.IsNullOrWhiteSpace(SelectedMainFont);

    public FontSelectionViewModel()
    {
        AvailableFonts = new ObservableCollection<string>(ScanAvailableFonts());
        AvailableTrueTypeFonts = new ObservableCollection<string>(ScanSystemTrueTypeFonts());
        LoadCurrentConfig();
    }

    internal static SortedSet<string> ScanAvailableFonts()
    {
        if (_cachedShxFonts != null) return _cachedShxFonts;

        var fonts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // 扫描 AutoCAD 支持搜索路径 (ACADPREFIX)
        try
        {
            var prefix = (string)AcadApp.GetSystemVariable("ACADPREFIX");
            if (!string.IsNullOrEmpty(prefix))
            {
                foreach (var dir in prefix.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    ScanDirectory(dir.Trim(), "*.shx", fonts);
                }
            }
        }
        catch { }

        // 扫描 AutoCAD 安装目录 Fonts 文件夹
        try
        {
            var processPath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (processPath != null)
            {
                var fontsDir = Path.Combine(Path.GetDirectoryName(processPath)!, "Fonts");
                ScanDirectory(fontsDir, "*.shx", fonts);
            }
        }
        catch { }

        _cachedShxFonts = fonts;
        return fonts;
    }

    /// <summary>
    /// 扫描系统已安装的 TrueType 字体，优先使用中文本地化名称。
    /// 通过 GlyphTypeface 交叉验证过滤元数据损坏的乱码字体名称。
    /// </summary>
    internal static SortedSet<string> ScanSystemTrueTypeFonts()
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

                // 优先使用 zh-CN 本地化名称，其次 zh
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

                // 跳过没有中文名的字体
                if (displayName == null) continue;

                // 基础字符过滤
                if (HasInvalidChars(displayName)) continue;

                // 通过 GlyphTypeface 交叉验证：
                // 读取字体文件内嵌的真实名称，如果显示名称不在其中，则为乱码
                if (!ValidateFontName(family, displayName)) continue;

                fonts.Add(displayName);
            }
        }
        catch { }
        _cachedTrueTypeFonts = fonts;
        return fonts;
    }

    /// <summary>
    /// 通过字体文件名交叉验证显示名称是否为乱码。
    /// 当文件名和显示名都包含 CJK 字符时，两者应至少共享一个汉字。
    /// 乱码名称（编码损坏）与文件名不会有任何共同汉字。
    /// 例：文件 "汉鼎简细等线.TTF" → 显示 "鞘湮楷札罟::潮瑟"，零交集 → 过滤。
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

            // 仅当文件名和显示名都含 CJK 字符时才做交叉验证
            if (!ContainsCjk(fileName) || !ContainsCjk(displayName))
                return true;

            // 两者应至少共享一个 CJK 字符，否则显示名为乱码
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

    private static bool ContainsCjk(string s)
    {
        foreach (char c in s)
        {
            if (c >= '\u4E00' && c <= '\u9FFF') return true;
        }
        return false;
    }

    /// <summary>
    /// 检查字体名称是否包含不合法字符。
    /// 白名单策略：只允许常规字母（排除 ModifierLetter）、数字和常见标点。
    /// ModifierLetter 包含看似冒号的修饰符（如 U+A789 ꞉），会导致乱码名称通过 IsLetter 检查。
    /// </summary>
    private static bool HasInvalidChars(string name)
    {
        foreach (char c in name)
        {
            if (char.IsDigit(c)) continue;
            if (char.IsLetter(c))
            {
                // ModifierLetter (Lm) 包含冒号变体等修饰符，不应出现在字体名中
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.ModifierLetter)
                    return true;
                continue;
            }
            if (c is ' ' or '-' or '_' or '(' or ')' or '（' or '）' or '.' or '·') continue;
            return true;
        }
        return false;
    }

    private static void ScanDirectory(string directory, string pattern, SortedSet<string> results)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                var name = Path.GetFileName(file);
                if (!string.IsNullOrEmpty(name))
                    results.Add(name);
            }
        }
        catch { }
    }

    /// <summary>从注册表读取当前配置作为默认选中项。</summary>
    private void LoadCurrentConfig()
    {
        var config = ConfigService.Instance;
        if (!string.IsNullOrEmpty(config.MainFont))
            SelectedMainFont = config.MainFont;
        if (!string.IsNullOrEmpty(config.BigFont))
            SelectedBigFont = config.BigFont;
        if (!string.IsNullOrEmpty(config.TrueTypeFont))
            SelectedTrueTypeFont = config.TrueTypeFont;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // 缓存 PropertyChangedEventArgs 避免重复分配
    private static readonly PropertyChangedEventArgs _mainFontArgs = new(nameof(SelectedMainFont));
    private static readonly PropertyChangedEventArgs _bigFontArgs = new(nameof(SelectedBigFont));
    private static readonly PropertyChangedEventArgs _trueTypeFontArgs = new(nameof(SelectedTrueTypeFont));
    private static readonly PropertyChangedEventArgs _confirmArgs = new(nameof(IsConfirmEnabled));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, propertyName switch
        {
            nameof(SelectedMainFont) => _mainFontArgs,
            nameof(SelectedBigFont) => _bigFontArgs,
            nameof(SelectedTrueTypeFont) => _trueTypeFontArgs,
            nameof(IsConfirmEnabled) => _confirmArgs,
            _ => new PropertyChangedEventArgs(propertyName)
        });
}
