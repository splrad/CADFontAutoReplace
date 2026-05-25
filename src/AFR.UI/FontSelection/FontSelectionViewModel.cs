using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AFR.Platform;
using AFR.Services;

namespace AFR.UI;

/// <summary>
/// 字体选择窗口的 ViewModel，管理 UI 状态与字体数据。
/// 不包含注册表操作 — 配置保存由命令层 (AfrCommands) 处理。
/// 通过 PlatformManager.FontScanner 获取可用字体，解耦平台依赖。
/// </summary>
internal sealed class FontSelectionViewModel : INotifyPropertyChanged
{
    private readonly UiRelayCommand _confirmCommand;
    private string _selectedMainFont = string.Empty;
    private string _selectedBigFont = string.Empty;
    private string _selectedTrueTypeFont = string.Empty;

    /// <summary>当前 CAD Fonts 目录下可用的 SHX 主字体列表（常规字体）。</summary>
    public ObservableCollection<string> AvailableMainFonts { get; }

    /// <summary>当前 CAD Fonts 目录下可用的 SHX 大字体列表。</summary>
    public ObservableCollection<string> AvailableBigFonts { get; }

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
            _confirmCommand?.RaiseCanExecuteChanged();
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

    public ICommand ConfirmCommand => _confirmCommand;

    public ICommand CancelCommand { get; }

    public event EventHandler<UiDialogCloseRequestedEventArgs>? CloseRequested;

    public FontSelectionViewModel()
    {
        _confirmCommand = new UiRelayCommand(() => RequestClose(true), () => IsConfirmEnabled);
        CancelCommand = new UiRelayCommand(() => RequestClose(false));

        AvailableMainFonts = new ObservableCollection<string>(ScanAvailableMainShxFonts());
        AvailableBigFonts = new ObservableCollection<string>(ScanAvailableBigShxFonts());
        AvailableTrueTypeFonts = new ObservableCollection<string>(ScanSystemTrueTypeFonts());
        LoadCurrentConfig();
    }

    /// <summary>
    /// 通过 PlatformManager.FontScanner 获取可用 SHX 主字体。
    /// </summary>
    internal static IReadOnlyCollection<string> ScanAvailableMainShxFonts()
    {
        return PlatformManager.FontScanner?.ScanAvailableMainShxFonts()
            ?? Array.Empty<string>();
    }

    /// <summary>
    /// 通过 PlatformManager.FontScanner 获取可用 SHX 大字体。
    /// </summary>
    internal static IReadOnlyCollection<string> ScanAvailableBigShxFonts()
    {
        return PlatformManager.FontScanner?.ScanAvailableBigShxFonts()
            ?? Array.Empty<string>();
    }

    /// <summary>
    /// 通过 PlatformManager.FontScanner 获取系统 TrueType 字体。
    /// </summary>
    internal static IReadOnlyCollection<string> ScanSystemTrueTypeFonts()
    {
        return PlatformManager.FontScanner?.ScanSystemTrueTypeFonts()
            ?? Array.Empty<string>();
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

    private void RequestClose(bool? dialogResult)
    {
        CloseRequested?.Invoke(this, new UiDialogCloseRequestedEventArgs(dialogResult));
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
