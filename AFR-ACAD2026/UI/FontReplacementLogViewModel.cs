using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AFR_ACAD2026.FontMapping;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.UI;

/// <summary>
/// 字体替换日志中的单行条目（每个缺失字体一行）。
/// 将"样式+主字体缺失"和"样式+大字体缺失"拆分为独立行，
/// 使表格布局紧凑统一。
/// </summary>
internal sealed class FontReplacementRow : INotifyPropertyChanged
{
    private string _selectedReplacement = string.Empty;

    public string StyleName { get; }
    public string FontCategory { get; }
    public string MissingFontName { get; }
    public bool IsTrueType { get; }
    public bool IsBigFont { get; }

    /// <summary>可供选择的替换字体列表（根据字体类型自动匹配）。</summary>
    public ObservableCollection<string> AvailableFonts { get; }

    public string SelectedReplacement
    {
        get => _selectedReplacement;
        set
        {
            if (_selectedReplacement == value) return;
            _selectedReplacement = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public FontReplacementRow(
        string styleName,
        string fontCategory,
        string missingFontName,
        bool isTrueType,
        bool isBigFont,
        ObservableCollection<string> availableFonts,
        string autoReplacement)
    {
        StyleName = styleName;
        FontCategory = fontCategory;
        MissingFontName = missingFontName;
        IsTrueType = isTrueType;
        IsBigFont = isBigFont;
        AvailableFonts = availableFonts;
        _selectedReplacement = autoReplacement;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 字体替换日志窗口的 ViewModel。
/// 将检测结果拆平为每个缺失字体一行，按类型分组排序（SHX → TrueType → 大字体）。
/// </summary>
internal sealed class FontReplacementLogViewModel : INotifyPropertyChanged
{
    private string _batchShxFont = string.Empty;
    private string _batchBigFont = string.Empty;
    private string _batchTrueTypeFont = string.Empty;

    public ObservableCollection<FontReplacementRow> Items { get; } = [];
    public ObservableCollection<InlineFontFixRecord> InlineFixItems { get; } = [];
    public string SummaryText { get; }
    public int ShxCount { get; }
    public int TrueTypeCount { get; }
    public int BigFontCount { get; }
    public int InlineFixCount { get; }
    public string ShxLabel => $"SHX主字体  {ShxCount}";
    public string TrueTypeLabel => $"TrueType  {TrueTypeCount}";
    public string BigFontLabel => $"SHX大字体  {BigFontCount}";
    public string InlineFixLabel => $"内联修复  {InlineFixCount}";
    public bool HasShx => ShxCount > 0;
    public bool HasTrueType => TrueTypeCount > 0;
    public bool HasBigFont => BigFontCount > 0;
    public bool HasInlineFix => InlineFixCount > 0;
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => !HasItems && !HasInlineFix;

    /// <summary>批量操作可选的 SHX 字体列表。</summary>
    public ObservableCollection<string> AvailableShxFonts { get; }

    /// <summary>批量操作可选的 TrueType 字体列表。</summary>
    public ObservableCollection<string> AvailableTrueTypeFonts { get; }

    public string BatchShxFont
    {
        get => _batchShxFont;
        set { if (_batchShxFont != value) { _batchShxFont = value ?? string.Empty; OnPropertyChanged(); } }
    }

    public string BatchBigFont
    {
        get => _batchBigFont;
        set { if (_batchBigFont != value) { _batchBigFont = value ?? string.Empty; OnPropertyChanged(); } }
    }

    public string BatchTrueTypeFont
    {
        get => _batchTrueTypeFont;
        set { if (_batchTrueTypeFont != value) { _batchTrueTypeFont = value ?? string.Empty; OnPropertyChanged(); } }
    }

    /// <summary>
    /// 将批量选择的字体填充到对应类型的所有行。
    /// 仅填充 ComboBox 选项，不直接写入数据库。
    /// </summary>
    public void ApplyBatch()
    {
        foreach (var row in Items)
        {
            if (row.IsTrueType && !string.IsNullOrEmpty(BatchTrueTypeFont))
                row.SelectedReplacement = BatchTrueTypeFont;
            else if (row.IsBigFont && !string.IsNullOrEmpty(BatchBigFont))
                row.SelectedReplacement = BatchBigFont;
            else if (!row.IsTrueType && !row.IsBigFont && !string.IsNullOrEmpty(BatchShxFont))
                row.SelectedReplacement = BatchShxFont;
        }
    }

    public FontReplacementLogViewModel(
        IReadOnlyList<FontCheckResult>? detectionResults,
        string globalMainFont,
        string globalBigFont,
        string globalTrueTypeFont,
        Dictionary<string, (string FileName, string BigFontFileName, string TypeFace)>? currentFonts = null,
        IReadOnlyList<InlineFontFixRecord>? inlineFixResults = null)
    {
        var shxFonts = new ObservableCollection<string>(FontSelectionViewModel.ScanAvailableFonts());
        var ttFonts = new ObservableCollection<string>(FontSelectionViewModel.ScanSystemTrueTypeFonts());
        AvailableShxFonts = shxFonts;
        AvailableTrueTypeFonts = ttFonts;

        if (detectionResults != null && detectionResults.Count > 0)
        {
            int ttCount = 0, shxCount = 0, bigCount = 0;
            var shxRows = new List<FontReplacementRow>();
            var ttRows = new List<FontReplacementRow>();
            var bigRows = new List<FontReplacementRow>();

            foreach (var r in detectionResults)
            {
                // 尝试获取该样式在图纸中的当前实际字体
                var current = (FileName: string.Empty, BigFontFileName: string.Empty, TypeFace: string.Empty);
                currentFonts?.TryGetValue(r.StyleName, out current);

                if (r.IsMainFontMissing)
                {
                    string category = r.IsTrueType ? "TrueType" : "SHX主字体";
                    string missingName = r.IsTrueType
                        ? (!string.IsNullOrEmpty(r.TypeFace) ? r.TypeFace : r.FileName)
                        : r.FileName;
                    var fonts = r.IsTrueType ? ttFonts : shxFonts;

                    // 优先使用当前实际字体，全局配置作为兜底
                    string replacement;
                    if (r.IsTrueType)
                    {
                        string currentTT = current.TypeFace;
                        replacement = !string.IsNullOrEmpty(currentTT) ? currentTT : globalTrueTypeFont;
                    }
                    else
                    {
                        string currentShx = current.FileName;
                        replacement = !string.IsNullOrEmpty(currentShx) ? currentShx : globalMainFont;
                    }

                    var row = new FontReplacementRow(
                        r.StyleName, category, missingName,
                        r.IsTrueType, false, fonts, replacement);

                    if (r.IsTrueType) { ttRows.Add(row); ttCount++; }
                    else { shxRows.Add(row); shxCount++; }
                }

                if (r.IsBigFontMissing)
                {
                    string currentBig = current.BigFontFileName;
                    string replacement = !string.IsNullOrEmpty(currentBig) ? currentBig : globalBigFont;

                    bigRows.Add(new FontReplacementRow(
                        r.StyleName, "SHX大字体", r.BigFontFileName,
                        false, true, shxFonts, replacement));
                    bigCount++;
                }
            }

            // 按类型分组排序：SHX主字体 → SHX大字体 → TrueType
            foreach (var row in shxRows) Items.Add(row);
            foreach (var row in bigRows) Items.Add(row);
            foreach (var row in ttRows) Items.Add(row);

            ShxCount = shxCount;
            TrueTypeCount = ttCount;
            BigFontCount = bigCount;

            int total = ttCount + shxCount + bigCount;
            SummaryText = $"{detectionResults.Count} 个样式 · {total} 个缺失";
        }
        else
        {
            SummaryText = "未检测到缺失字体";
        }

        // 内联字体修复记录
        if (inlineFixResults != null)
        {
            foreach (var r in inlineFixResults)
                InlineFixItems.Add(r);
            InlineFixCount = inlineFixResults.Count;

            if (Items.Count == 0 && InlineFixCount > 0)
                SummaryText = $"内联字体修复 {InlineFixCount} 项";
            else if (InlineFixCount > 0)
                SummaryText += $" · 内联修复 {InlineFixCount} 项";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
