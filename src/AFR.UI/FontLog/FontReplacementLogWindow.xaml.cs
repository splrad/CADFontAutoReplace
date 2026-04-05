using System.Windows;
using System.Windows.Input;
using AFR.Models;
using AFR.Platform;

namespace AFR.UI;

/// <summary>
/// 字体替换日志窗口。
/// 显示缺失字体检测结果，支持手动逐一指定替换字体。
/// 通过 ApplyReplacementsHandler 回调解耦 CAD 平台操作。
/// </summary>
public partial class FontReplacementLogWindow : Window
{
    public FontReplacementLogViewModel ViewModel { get; }

    /// <summary>累计成功替换的样式数量。</summary>
    public int AppliedCount { get; private set; }

    /// <summary>
    /// 替换操作回调。接收替换列表，返回成功替换的数量。
    /// 由调用方提供平台特定的实现（如 AutoCAD 的文档锁定与字体替换）。
    /// </summary>
    public Func<IReadOnlyList<StyleFontReplacement>, int>? ApplyReplacementsHandler { get; set; }

    public FontReplacementLogWindow(FontReplacementLogViewModel vm)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (ApplyReplacementsHandler == null) return;

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
                int count = ApplyReplacementsHandler(map.Values.ToList());
                AppliedCount += count;
            }
        }
        catch (Exception ex)
        {
            PlatformManager.Logger?.Error("手动替换字体失败", ex);
        }
    }

    private void OnBatchApply(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplyBatch();
    }

    private void OnComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox combo)
        {
            combo.ApplyTemplate();
            var textBox = combo.Template.FindName("PART_EditableTextBox", combo) as System.Windows.Controls.TextBox;
            if (textBox != null)
                textBox.SelectionBrush = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void OnTablePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer scrollViewer)
        {
            // 主表固定行高约为 25 像素 (ComboBox Height 22 + Padding 1+1 + Border 1)
            double rowHeight = 25.0;

            // 每次滚动 3 行
            double step = rowHeight * 3.0;

            // 计算新的偏移量并修正到整行边界，实现“整行滚动”触感
            double newOffset = scrollViewer.VerticalOffset - Math.Sign(e.Delta) * step;
            newOffset = Math.Round(newOffset / rowHeight) * rowHeight;

            if (newOffset < 0) newOffset = 0;
            if (newOffset > scrollViewer.ScrollableHeight) newOffset = scrollViewer.ScrollableHeight;

            scrollViewer.ScrollToVerticalOffset(newOffset);
            e.Handled = true;
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
