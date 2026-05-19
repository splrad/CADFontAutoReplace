using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AFR.Models;
using AFR.Platform;
using AFR.Services;

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
    private readonly UiRelayCommand _applyCommand;
    private string _batchShxFont = string.Empty;
    private string _batchBigFont = string.Empty;
    private string _batchTrueTypeFont = string.Empty;
    private bool _hasUserChanges;

    public ObservableCollection<FontReplacementRow> Items { get; } = new();
    public ObservableCollection<InlineFontFixRecord> InlineFixItems { get; } = new();
    public ObservableCollection<RuntimeFontMappingRecord> RuntimeMappingItems { get; } = new();
    public string SummaryText { get; }
    public int ShxCount { get; }
    public int TrueTypeCount { get; }
    public int BigFontCount { get; }
    public int InlineFixCount { get; }
    public int RuntimeMappingCount { get; }
    /// <summary>未成功替换的字体数量。</summary>
    public int FailedCount { get; }
    /// <summary>已成功替换的字体数量。</summary>
    public int ReplacedCount { get; }
    public string ShxLabel => $"SHX主字体  {ShxCount}";
    public string TrueTypeLabel => $"TrueType  {TrueTypeCount}";
    public string BigFontLabel => $"SHX大字体  {BigFontCount}";
    public string InlineFixLabel => $"MText映射  {InlineFixCount}";
    public string RuntimeMappingLabel => $"临时映射  {RuntimeMappingCount}";
    public string FailedLabel => $"未替换  {FailedCount}";
    public bool HasShx => ShxCount > 0;
    public bool HasTrueType => TrueTypeCount > 0;
    public bool HasBigFont => BigFontCount > 0;
    public bool HasInlineFix => InlineFixCount > 0;
    public bool HasRuntimeMapping => RuntimeMappingCount > 0;
    public bool HasFailed => FailedCount > 0;
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => !HasItems && !HasInlineFix && !HasRuntimeMapping;
    public bool HasAnyContent => HasItems || HasInlineFix || HasRuntimeMapping;

    public ICommand ApplyCommand => _applyCommand;

    public ICommand ApplyBatchCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>
    /// 替换操作回调。接收替换列表，返回成功替换的数量。
    /// 由调用方提供平台特定的实现（如 AutoCAD 的文档锁定与字体替换）。
    /// </summary>
    public Func<IReadOnlyList<StyleFontReplacement>, int>? ApplyReplacementsHandler { get; set; }

    /// <summary>
    /// 刷新回调。应用替换成功后调用，返回新的 ViewModel 以刷新界面。
    /// 由调用方提供平台特定的实现（如重新检测缺失字体并构建新 ViewModel）。
    /// </summary>
    public Func<FontReplacementLogViewModel>? RefreshHandler { get; set; }

    /// <summary>累计成功替换的样式数量。</summary>
    public int AppliedCount { get; private set; }

    /// <summary>最近一次应用替换时的替换指令列表，供调用方生成统计日志。</summary>
    public IReadOnlyList<StyleFontReplacement>? LastAppliedReplacements { get; private set; }

    /// <summary>是否有任何行的替换字体被用户修改过（与初始值不同）。</summary>
    public bool HasUserChanges
    {
        get => _hasUserChanges;
        private set
        {
            if (_hasUserChanges == value) return;

            _hasUserChanges = value;
            OnPropertyChanged();
            _applyCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>批量操作可选的 SHX 主字体列表（常规字体）。</summary>
    public ObservableCollection<string> AvailableMainFonts { get; }

    /// <summary>批量操作可选的 SHX 大字体列表。</summary>
    public ObservableCollection<string> AvailableBigFonts { get; }

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

    private void ApplyReplacements()
    {
        if (ApplyReplacementsHandler == null) return;

        try
        {
            var replacements = BuildReplacementRequests();
            DiagnosticLogger.Info("UI", $"应用替换: 从 {Items.Count} 行构建 {replacements.Count} 条替换指令");
            foreach (var replacement in replacements)
            {
                DiagnosticLogger.Info("UI",
                    $"  样式='{replacement.StyleName}' Main='{replacement.MainFontReplacement}' Big='{replacement.BigFontReplacement}' IsTT={replacement.IsTrueType}");
            }

            if (replacements.Count == 0)
                return;

            int count = ApplyReplacementsHandler(replacements);
            AppliedCount += count;
            LastAppliedReplacements = replacements;
            DiagnosticLogger.Info("UI", $"Handler 返回: {count}, 累计 AppliedCount={AppliedCount}");
            OnPropertyChanged(nameof(AppliedCount));
            OnPropertyChanged(nameof(LastAppliedReplacements));

            if (count <= 0 || RefreshHandler == null)
                return;

            try
            {
                var newViewModel = RefreshHandler();
                newViewModel.ApplyReplacementsHandler = ApplyReplacementsHandler;
                newViewModel.RefreshHandler = RefreshHandler;
                newViewModel.AppliedCount = AppliedCount;
                newViewModel.LastAppliedReplacements = LastAppliedReplacements;
                DiagnosticLogger.Info("UI", $"刷新完成: Items={newViewModel.Items.Count} 未替换={newViewModel.FailedCount}");
                ViewModelRefreshed?.Invoke(this, newViewModel);
            }
            catch (Exception refreshEx)
            {
                DiagnosticLogger.LogError("UI 刷新失败", refreshEx);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError("UI 应用替换失败", ex);
            PlatformManager.Logger?.Error("手动替换字体失败", ex);
        }
    }

    private IReadOnlyList<StyleFontReplacement> BuildReplacementRequests()
    {
        var map = new Dictionary<string, StyleFontReplacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Items)
        {
            string font = row.SelectedReplacement?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(font)) continue;

            // 已替换行仅在用户修改了选择时才纳入，避免重复提交未变更的替换。
            if (row.IsReplaced && string.Equals(font, row.OriginalReplacement?.Trim(),
                StringComparison.OrdinalIgnoreCase)) continue;

            if (!map.TryGetValue(row.StyleName, out var existing))
                existing = new StyleFontReplacement(row.StyleName, false, string.Empty, string.Empty);

            map[row.StyleName] = row.IsBigFont
                ? existing with { BigFontReplacement = font }
                : existing with { MainFontReplacement = font, IsTrueType = row.IsTrueType };
        }

        return map.Values.ToList();
    }

    public FontReplacementLogViewModel(
        IReadOnlyList<FontCheckResult>? detectionResults,
        string globalMainFont,
        string globalBigFont,
        string globalTrueTypeFont,
        Dictionary<string, (string FileName, string BigFontFileName, string TypeFace)>? currentFonts = null,
        IReadOnlyList<InlineFontFixRecord>? inlineFixResults = null,
        IReadOnlyList<RuntimeFontMappingRecord>? runtimeMappingResults = null,
        HashSet<string>? stillMissingStyleNames = null)
    {
        _applyCommand = new UiRelayCommand(ApplyReplacements, () => HasUserChanges);
        ApplyBatchCommand = new UiRelayCommand(ApplyBatch);
        CloseCommand = new UiRelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        // 触发扫描（确保 FontCache 已填充）
        FontSelectionViewModel.EnsureFontCachePopulated();
        var mainFonts = new ObservableCollection<string>(FontManager.GetMainFontSnapshot());
        var bigFonts = new ObservableCollection<string>(FontManager.GetBigFontSnapshot());
        var ttFonts = new ObservableCollection<string>(FontSelectionViewModel.ScanSystemTrueTypeFonts());
        AvailableMainFonts = mainFonts;
        AvailableBigFonts = bigFonts;
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
                    var fonts = r.IsTrueType ? ttFonts : mainFonts;

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
                        false, true, isReplaced, bigFonts, replacement);
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

        // 监听每行的 SelectedReplacement 变化，驱动 HasUserChanges 更新
        foreach (var row in Items)
            row.PropertyChanged += OnRowPropertyChanged;

        // 运行时字体映射记录（只读展示，不参与手动替换）
        if (runtimeMappingResults != null)
        {
            foreach (var r in runtimeMappingResults
                         .OrderBy(r => r.StyleName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(r => r.OriginalFont, StringComparer.OrdinalIgnoreCase))
                RuntimeMappingItems.Add(r);
            RuntimeMappingCount = runtimeMappingResults.Count;

            if (Items.Count == 0 && RuntimeMappingCount > 0)
                SummaryText = $"样式表 @ 字体临时映射 {RuntimeMappingCount} 项";
            else if (RuntimeMappingCount > 0)
                SummaryText += $" · 临时映射 {RuntimeMappingCount} 项";
        }

        // 内联字体修复记录（按类型排序：SHX主字体 → SHX大字体 → TrueType）
        if (inlineFixResults != null)
        {
            foreach (var r in inlineFixResults.OrderBy(r => r.FontCategory switch
            {
                "SHX主字体" => 0,
                "SHX大字体" => 1,
                _ => 2   // TrueType、TrueType映射 等
            }))
                InlineFixItems.Add(r);
            InlineFixCount = inlineFixResults.Count;

            if (Items.Count == 0 && RuntimeMappingCount == 0 && InlineFixCount > 0)
                SummaryText = $"内联字体修复 {InlineFixCount} 项";
            else if (InlineFixCount > 0)
                SummaryText += $" · 内联修复 {InlineFixCount} 项";
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FontReplacementRow.SelectedReplacement))
            HasUserChanges = EvaluateHasUserChanges();
    }

    private bool EvaluateHasUserChanges()
    {
        foreach (var row in Items)
        {
            string current = row.SelectedReplacement?.Trim() ?? string.Empty;
            string original = row.OriginalReplacement?.Trim() ?? string.Empty;
            if (!string.Equals(current, original, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<FontReplacementLogViewModel>? ViewModelRefreshed;

    public event EventHandler? CloseRequested;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
