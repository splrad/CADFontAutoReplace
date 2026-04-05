using System.Windows;
using System.Windows.Input;
using AFR.Models;
using AFR.Platform;
using AFR.Services;

namespace AFR.UI;

/// <summary>
/// 字体替换日志窗口。
/// 显示缺失字体检测结果，支持手动逐一指定替换字体。
/// 通过 ApplyReplacementsHandler 回调解耦 CAD 平台操作。
/// </summary>
public partial class FontReplacementLogWindow : Window
{
    private bool _comboBoxDropDownOpen;

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

                // 已替换行仅在用户修改了选择时才纳入（避免重复提交未变更的替换）
                if (row.IsReplaced && string.Equals(font, row.OriginalReplacement?.Trim(),
                    StringComparison.OrdinalIgnoreCase)) continue;

                if (!map.TryGetValue(row.StyleName, out var existing))
                    existing = new StyleFontReplacement(row.StyleName, false, string.Empty, string.Empty);

                map[row.StyleName] = row.IsBigFont
                    ? existing with { BigFontReplacement = font }
                    : existing with { MainFontReplacement = font, IsTrueType = row.IsTrueType };
            }

            DiagnosticLogger.Info("UI", $"应用替换: 从 {ViewModel.Items.Count} 行构建 {map.Count} 条替换指令");
            foreach (var (name, rep) in map)
            {
                DiagnosticLogger.Info("UI",
                    $"  样式='{name}' Main='{rep.MainFontReplacement}' Big='{rep.BigFontReplacement}' IsTT={rep.IsTrueType}");
            }

            if (map.Count > 0)
            {
                int count = ApplyReplacementsHandler(map.Values.ToList());
                AppliedCount += count;
                DiagnosticLogger.Info("UI", $"Handler 返回: {count}, 累计 AppliedCount={AppliedCount}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError("UI 应用替换失败", ex);
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

            combo.DropDownOpened += (_, _) => _comboBoxDropDownOpen = true;
            combo.DropDownClosed += (_, _) => _comboBoxDropDownOpen = false;
        }
    }

    private void OnTablePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // ComboBox 下拉列表打开时，让其内部 ScrollViewer 处理滚动
        if (_comboBoxDropDownOpen) return;

        if (sender is System.Windows.Controls.ScrollViewer scrollViewer)
        {
            // 动态探测真实行高（ComboBox 和 Padding 会将行高撑到 30px 或随 DPI 变化）
            double rowHeight = 30.0;

            if (scrollViewer.Content is System.Windows.Controls.StackPanel stackPanel &&
                stackPanel.Children.Count > 0 && 
                stackPanel.Children[0] is System.Windows.Controls.ItemsControl itemsControl &&
                itemsControl.Items.Count > 0)
            {
                var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
                if (container != null && container.ActualHeight > 0)
                {
                    rowHeight = container.ActualHeight;
                }
            }

            // 每次滚动 3 行的真实高度
            double step = rowHeight * 3.0;

            // 计算新的偏移量并修正到整行边界
            double newOffset = scrollViewer.VerticalOffset - Math.Sign(e.Delta) * step;
            newOffset = Math.Round(newOffset / rowHeight) * rowHeight;

            if (newOffset < 0) newOffset = 0;
            if (newOffset > scrollViewer.ScrollableHeight) newOffset = scrollViewer.ScrollableHeight;

            scrollViewer.ScrollToVerticalOffset(newOffset);
            e.Handled = true;
        }
    }

    private void OnTableLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer scrollViewer &&
            scrollViewer.Content is System.Windows.Controls.StackPanel stackPanel &&
            stackPanel.Children.Count > 0 &&
            stackPanel.Children[0] is System.Windows.Controls.ItemsControl itemsControl)
        {
            // 延缓到底层 UI 完全渲染后，以提取真实的物理像素高度
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (itemsControl.Items.Count > 0 && itemsControl.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement container && container.ActualHeight > 0)
                {
                    // 动态获取因 Windows 缩放（DPI）、字体渲染引擎可能引起的略微波动的单行真实行高，并乘以需求行距
                    scrollViewer.Height = Math.Ceiling(container.ActualHeight * 12.0);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
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
