using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.UI;

/// <summary>
/// 字体替换日志中的单个样式条目。
/// 显示缺失字体信息并提供手动替换选项。
/// </summary>
internal sealed class FontStyleLogItem : INotifyPropertyChanged
{
    private string _selectedMainReplacement = string.Empty;
    private string _selectedBigReplacement = string.Empty;
    private bool _isApplied;

    public string StyleName { get; }
    public string OriginalFont { get; }
    public string OriginalBigFont { get; }
    public bool IsMainFontMissing { get; }
    public bool IsBigFontMissing { get; }
    public bool IsTrueType { get; }
    public string FontTypeTag => IsTrueType ? "TrueType" : "SHX";

    /// <summary>可供选择的主字体列表（根据字体类型自动为 SHX 或 TrueType 列表）。</summary>
    public ObservableCollection<string> AvailableMainFonts { get; }

    /// <summary>可供选择的大字体列表（始终为 SHX 字体）。</summary>
    public ObservableCollection<string> AvailableBigFonts { get; }

    public string SelectedMainReplacement
    {
        get => _selectedMainReplacement;
        set
        {
            if (_selectedMainReplacement == value) return;
            _selectedMainReplacement = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public string SelectedBigReplacement
    {
        get => _selectedBigReplacement;
        set
        {
            if (_selectedBigReplacement == value) return;
            _selectedBigReplacement = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public bool IsApplied
    {
        get => _isApplied;
        set
        {
            if (_isApplied == value) return;
            _isApplied = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => IsApplied ? "✓ 已替换" : "待处理";

    public FontStyleLogItem(
        FontCheckResult result,
        string autoReplacedMainFont,
        string autoReplacedBigFont,
        ObservableCollection<string> shxFonts,
        ObservableCollection<string> trueTypeFonts)
    {
        StyleName = result.StyleName;
        OriginalFont = result.IsTrueType
            ? (!string.IsNullOrEmpty(result.TypeFace) ? result.TypeFace : result.FileName)
            : result.FileName;
        OriginalBigFont = result.BigFontFileName;
        IsMainFontMissing = result.IsMainFontMissing;
        IsBigFontMissing = result.IsBigFontMissing;
        IsTrueType = result.IsTrueType;

        AvailableMainFonts = result.IsTrueType ? trueTypeFonts : shxFonts;
        AvailableBigFonts = shxFonts;

        _selectedMainReplacement = autoReplacedMainFont;
        _selectedBigReplacement = autoReplacedBigFont;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 字体替换日志窗口的 ViewModel。
/// 管理缺失字体列表、可用字体列表及统计信息。
/// </summary>
internal sealed class FontReplacementLogViewModel : INotifyPropertyChanged
{
    public ObservableCollection<FontStyleLogItem> Items { get; } = [];
    public string SummaryText { get; }
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => !HasItems;

    public FontReplacementLogViewModel(
        IReadOnlyList<FontCheckResult>? detectionResults,
        string globalMainFont,
        string globalBigFont,
        string globalTrueTypeFont)
    {
        var shxFonts = new ObservableCollection<string>(FontSelectionViewModel.ScanAvailableFonts());
        var ttFonts = new ObservableCollection<string>(FontSelectionViewModel.ScanSystemTrueTypeFonts());

        if (detectionResults != null && detectionResults.Count > 0)
        {
            int ttCount = 0, shxCount = 0, bigCount = 0;
            foreach (var r in detectionResults)
            {
                string autoMain = r.IsMainFontMissing
                    ? (r.IsTrueType ? globalTrueTypeFont : globalMainFont)
                    : string.Empty;
                string autoBig = r.IsBigFontMissing ? globalBigFont : string.Empty;

                Items.Add(new FontStyleLogItem(r, autoMain, autoBig, shxFonts, ttFonts));

                if (r.IsMainFontMissing)
                {
                    if (r.IsTrueType) ttCount++;
                    else shxCount++;
                }
                if (r.IsBigFontMissing) bigCount++;
            }

            int total = ttCount + shxCount + bigCount;
            SummaryText = $"检测到 {Items.Count} 个样式共 {total} 个缺失字体 (TrueType: {ttCount}, SHX: {shxCount}, 大字体: {bigCount})";
        }
        else
        {
            SummaryText = "当前图纸未检测到缺失字体。";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
