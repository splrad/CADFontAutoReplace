using System.Diagnostics;
using System.IO;
using System.Windows;
using AFR_ACAD2026.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.UI;

public partial class FontSelectionWindow : Window
{
    public string SelectedMainFont { get; private set; } = string.Empty;
    public string SelectedBigFont { get; private set; } = string.Empty;
    public bool Confirmed { get; private set; }

    public FontSelectionWindow()
    {
        InitializeComponent();
        LoadAvailableFonts();
        LoadCurrentConfig();
    }

    private void LoadAvailableFonts()
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

        // 扫描 AutoCAD 安装目录下的 Fonts 文件夹
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

        var fontList = fonts.ToList();
        MainFontCombo.ItemsSource = fontList;
        BigFontCombo.ItemsSource = fontList;
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

    private void LoadCurrentConfig()
    {
        var config = ConfigService.Instance;
        if (!string.IsNullOrEmpty(config.MainFont))
            MainFontCombo.Text = config.MainFont;
        if (!string.IsNullOrEmpty(config.BigFont))
            BigFontCombo.Text = config.BigFont;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedMainFont = MainFontCombo.Text?.Trim() ?? string.Empty;
        SelectedBigFont = BigFontCombo.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(SelectedMainFont))
        {
            MessageBox.Show(
                "请选择或输入主字体名称。",
                "验证提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
