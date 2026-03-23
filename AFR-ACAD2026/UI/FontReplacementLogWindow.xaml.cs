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
            var replacements = new List<StyleFontReplacement>();
            foreach (var item in ViewModel.Items)
            {
                if (item.IsApplied) continue;

                string mainFont = item.IsMainFontMissing
                    ? (item.SelectedMainReplacement?.Trim() ?? string.Empty)
                    : string.Empty;
                string bigFont = item.IsBigFontMissing
                    ? (item.SelectedBigReplacement?.Trim() ?? string.Empty)
                    : string.Empty;

                if (!string.IsNullOrEmpty(mainFont) || !string.IsNullOrEmpty(bigFont))
                {
                    replacements.Add(new StyleFontReplacement(
                        item.StyleName, item.IsTrueType, mainFont, bigFont));
                }
            }

            if (replacements.Count > 0)
            {
                using (doc.LockDocument())
                {
                    int count = FontReplacer.ReplaceByStyleMapping(doc.Database, replacements);
                    if (count > 0)
                    {
                        doc.Editor.Regen();
                        AppliedCount += count;
                    }
                }

                foreach (var item in ViewModel.Items)
                {
                    if (!item.IsApplied)
                        item.IsApplied = true;
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
