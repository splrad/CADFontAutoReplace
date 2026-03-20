using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using AFR_ACAD2026.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.UI;

/// <summary>
/// 字体选择窗口的 ViewModel，管理 UI 状态与字体数据。
/// 不包含注册表操作 — 配置保存由命令层 (AfrCommands) 处理。
/// </summary>
internal sealed class FontSelectionViewModel : INotifyPropertyChanged
{
    private string _selectedMainFont = string.Empty;
    private string _selectedBigFont = string.Empty;

    /// <summary>当前 CAD Fonts 目录下可用的 SHX 字体列表。</summary>
    public ObservableCollection<string> AvailableFonts { get; }

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

    /// <summary>主字体已选择时启用确认按钮。</summary>
    public bool IsConfirmEnabled => !string.IsNullOrWhiteSpace(SelectedMainFont);

    public FontSelectionViewModel()
    {
        AvailableFonts = new ObservableCollection<string>(ScanAvailableFonts());
        LoadCurrentConfig();
    }

    private static SortedSet<string> ScanAvailableFonts()
    {
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

        return fonts;
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // 缓存 PropertyChangedEventArgs 避免重复分配
    private static readonly PropertyChangedEventArgs _mainFontArgs = new(nameof(SelectedMainFont));
    private static readonly PropertyChangedEventArgs _bigFontArgs = new(nameof(SelectedBigFont));
    private static readonly PropertyChangedEventArgs _confirmArgs = new(nameof(IsConfirmEnabled));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, propertyName switch
        {
            nameof(SelectedMainFont) => _mainFontArgs,
            nameof(SelectedBigFont) => _bigFontArgs,
            nameof(IsConfirmEnabled) => _confirmArgs,
            _ => new PropertyChangedEventArgs(propertyName)
        });
}
