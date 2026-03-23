using System.Windows;
using System.Windows.Input;
using AFR_ACAD2026.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.UI;

/// <summary>
/// 字体替换日志窗口。
/// 显示缺失字体检测结果，支持手动逐一指定替换字体。
/// </summary>
public partial class FontReplacementLogWindow : Window
{
    internal FontReplacementLogViewModel ViewModel { get; }

    /// <summary>累计成功替换的样式数量。</summary>
    public int AppliedCount { get; private set; }

    internal FontReplacementLogWindow(FontReplacementLogViewModel vm)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        try
        {
            // 按样式名称分组，将主字体行和大字体行合并为一条替换指令
            var map = new Dictionary<string, StyleFontReplacement>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ViewModel.Items)
            {
                string font = row.SelectedReplacement?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(font)) continue;

                if (!map.TryGetValue(row.StyleName, out var existing))
                    existing = new StyleFontReplacement(row.StyleName, false, string.Empty, string.Empty);

                map[row.StyleName] = row.IsBigFont
                    ? existing with { BigFontReplacement = font }
                    : existing with { MainFontReplacement = font, IsTrueType = row.IsTrueType };
            }

            if (map.Count > 0)
            {
                var replacements = map.Values.ToList();
                using (doc.LockDocument())
                {
                    int count = FontReplacer.ReplaceByStyleMapping(doc.Database, replacements);
                    if (count > 0)
                    {
                        doc.Editor.Regen();
                        AppliedCount += count;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("手动替换字体失败", ex);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
