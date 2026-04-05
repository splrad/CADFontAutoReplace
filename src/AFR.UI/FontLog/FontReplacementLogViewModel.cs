using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AFR.Models;

namespace AFR.UI;

/// <summary>
/// 字体替换日志中的单行条目（每个缺失字体一行）。
/// 将"样式+主字体缺失"和"样式+大字体缺失"拆分为独立行，
/// 使表格布局紧凑统一。
/// </summary>
public sealed class FontReplacementRow : INotifyPropertyChanged
{
    private string _selectedReplacement = string.Empty;

    public string StyleName { get; }
    public string FontCategory { get; }
    public string MissingFontName { get; }
    public bool IsTrueType { get; }
    public bool IsBigFont { get; }

    /// <summary>该缺失字体是否已被成功替换。</summary>
    public bool IsReplaced { get; }

    /// <summary>构造时预填的初始替换字体（用于判断用户是否修改了选择）。</summary>
    public string OriginalReplacement { get; }

    /// <summary>显示用缺失字体名（未替换时加 ⚠ 前缀）。</summary>
    public string DisplayMissingFontName => IsReplaced ? MissingFontName : $"⚠ {MissingFontName}";

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
        bool isReplaced,
        ObservableCollection<string> availableFonts,
        string autoReplacement)
    {
        StyleName = styleName;
        FontCategory = fontCategory;
        MissingFontName = missingFontName;
        IsTrueType = isTrueType;
        IsBigFont = isBigFont;
        IsReplaced = isReplaced;
        AvailableFonts = availableFonts;
        OriginalReplacement = autoReplacement;
        _selectedReplacement = autoReplacement;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 字体替换日志窗口的 ViewModel。
/// 将检测结果拆平为每个缺失字体一行，按类型分组排序（SHX → TrueType → 大字体）。
/// 通过 FontSelectionViewModel 获取可用字体列表，解耦平台依赖。
/// </summary>
public sealed class FontReplacementLogViewModel : INotifyPropertyChanged
{
    private string _batchShxFont = string.Empty;
    private string _batchBigFont = string.Empty;
    private string _batchTrueTypeFont = string.Empty;

    public ObservableCollection<FontReplacementRow> Items { get; } = new();
    public ObservableCollection<InlineFontFixRecord> InlineFixItems { get; } = new();
    public string SummaryText { get; }
    public int ShxCount { get; }
    public int TrueTypeCount { get; }
    public int BigFontCount { get; }
    public int InlineFixCount { get; }
    /// <summary>未成功替换的字体数量。</summary>
    public int FailedCount { get; }
    /// <summary>已成功替换的字体数量。</summary>
    public int ReplacedCount { get; }
    public string ShxLabel => $"SHX主字体  {ShxCount}";
    public string TrueTypeLabel => $"TrueType  {TrueTypeCount}";
    public string BigFontLabel => $"SHX大字体  {BigFontCount}";
    public string InlineFixLabel => $"MText映射  {InlineFixCount}";
    public string FailedLabel => $"未替换  {FailedCount}";
    public bool HasShx => ShxCount > 0;
    public bool HasTrueType => TrueTypeCount > 0;
    public bool HasBigFont => BigFontCount > 0;
    public bool HasInlineFix => InlineFixCount > 0;
    public bool HasFailed => FailedCount > 0;
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
        IReadOnlyList<InlineFontFixRecord>? inlineFixResults = null,
        HashSet<string>? stillMissingStyleNames = null)
    {
        var shxFonts = new ObservableCollection<string>(FontSelectionViewModel.ScanAvailableFonts());
        var ttFonts = new ObservableCollection<string>(FontSelectionViewModel.ScanSystemTrueTypeFonts());
        AvailableShxFonts = shxFonts;
        AvailableTrueTypeFonts = ttFonts;

        if (detectionResults != null && detectionResults.Count > 0)
        {
            int ttCount = 0, shxCount = 0, bigCount = 0;
            int failedCount = 0, replacedCount = 0;
            // 每组内部按类型分桶：SHX主字体 → SHX大字体 → TrueType
            var failedShx = new List<FontReplacementRow>();
            var failedBig = new List<FontReplacementRow>();
            var failedTT = new List<FontReplacementRow>();
            var replacedShx = new List<FontReplacementRow>();
            var replacedBig = new List<FontReplacementRow>();
            var replacedTT = new List<FontReplacementRow>();

            foreach (var r in detectionResults)
            {
                // 尝试获取该样式在图纸中的当前实际字体
                var current = (FileName: string.Empty, BigFontFileName: string.Empty, TypeFace: string.Empty);
                currentFonts?.TryGetValue(r.StyleName, out current);

                // 判断该样式是否仍然缺失（未成功替换）
                bool isStillMissing = stillMissingStyleNames != null
                    && stillMissingStyleNames.Contains(r.StyleName);
                bool isReplaced = !isStillMissing;

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
                        r.IsTrueType, false, isReplaced, fonts, replacement);

                    if (r.IsTrueType) ttCount++;
                    else shxCount++;

                    if (isReplaced)
                    {
                        (r.IsTrueType ? replacedTT : replacedShx).Add(row);
                        replacedCount++;
                    }
                    else
                    {
                        (r.IsTrueType ? failedTT : failedShx).Add(row);
                        failedCount++;
                    }
                }

                // TrueType 样式不支持大字体，不显示大字体行
                if (r.IsBigFontMissing && !r.IsTrueType)
                {
                    string currentBig = current.BigFontFileName;
                    string replacement = !string.IsNullOrEmpty(currentBig) ? currentBig : globalBigFont;

                    var row = new FontReplacementRow(
                        r.StyleName, "SHX大字体", r.BigFontFileName,
                        false, true, isReplaced, shxFonts, replacement);
                    bigCount++;

                    if (isReplaced) { replacedBig.Add(row); replacedCount++; }
                    else { failedBig.Add(row); failedCount++; }
                }
            }

            // 排序：未替换置顶(SHX→大字体→TrueType) → 已替换在后(SHX→大字体→TrueType)
            foreach (var row in failedShx) Items.Add(row);
            foreach (var row in failedBig) Items.Add(row);
            foreach (var row in failedTT) Items.Add(row);
            foreach (var row in replacedShx) Items.Add(row);
            foreach (var row in replacedBig) Items.Add(row);
            foreach (var row in replacedTT) Items.Add(row);

            ShxCount = shxCount;
            TrueTypeCount = ttCount;
            BigFontCount = bigCount;
            FailedCount = failedCount;
            ReplacedCount = replacedCount;

            int total = ttCount + shxCount + bigCount;
            if (failedCount > 0)
                SummaryText = $"{total} 个缺失 · 已替换 {replacedCount} · 未替换 {failedCount}";
            else
                SummaryText = $"{total} 个缺失 · 全部已替换";
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
