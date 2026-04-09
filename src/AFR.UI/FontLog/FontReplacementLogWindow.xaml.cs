using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>最近一次应用替换时的替换指令列表，供调用方生成统计日志。</summary>
    public IReadOnlyList<StyleFontReplacement>? LastAppliedReplacements { get; private set; }

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

    public FontReplacementLogWindow(FontReplacementLogViewModel vm)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();
        WindowPositionHelper.SetupCenterOnParent(this);

        // 确保窗口不超过屏幕可用高度
        double workArea = SystemParameters.WorkArea.Height;
        if (Height > workArea)
            Height = workArea;
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
                    ? existing.With(bigFontReplacement: font)
                    : existing.With(mainFontReplacement: font, isTrueType: row.IsTrueType);
            }

            DiagnosticLogger.Info("UI", $"应用替换: 从 {ViewModel.Items.Count} 行构建 {map.Count} 条替换指令");
            foreach (var kvp in map)
            {
                var name = kvp.Key;
                var rep = kvp.Value;
                DiagnosticLogger.Info("UI",
                    $"  样式='{name}' Main='{rep.MainFontReplacement}' Big='{rep.BigFontReplacement}' IsTT={rep.IsTrueType}");
            }

            if (map.Count > 0)
            {
                var list = map.Values.ToList();
                int count = ApplyReplacementsHandler(list);
                AppliedCount += count;
                LastAppliedReplacements = list;
                DiagnosticLogger.Info("UI", $"Handler 返回: {count}, 累计 AppliedCount={AppliedCount}");

                // 替换成功后刷新界面（重新检测并重建 ViewModel）
                if (count > 0 && RefreshHandler != null)
                {
                    try
                    {
                        var newVm = RefreshHandler();
                        DataContext = newVm;
                        DiagnosticLogger.Info("UI", $"刷新完成: Items={newVm.Items.Count} 未替换={newVm.FailedCount}");
                    }
                    catch (Exception refreshEx)
                    {
                        DiagnosticLogger.LogError("UI 刷新失败", refreshEx);
                    }
                }
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

        if (sender is ScrollViewer scrollViewer)
        {
            double rowHeight = GetRowHeight();
            double step = rowHeight * 3.0;

            double newOffset = scrollViewer.VerticalOffset - Math.Sign(e.Delta) * step;
            newOffset = Math.Round(newOffset / rowHeight) * rowHeight;

            if (newOffset < 0) newOffset = 0;
            if (newOffset > scrollViewer.ScrollableHeight) newOffset = scrollViewer.ScrollableHeight;

            scrollViewer.ScrollToVerticalOffset(newOffset);
            e.Handled = true;
        }
    }

    /// <summary>动态获取单行真实行高（从样式表或 MText 数据行中探测）。</summary>
    private double GetRowHeight()
    {
        // 优先从样式表数据行探测
        if (StyleDataRows.Items.Count > 0)
        {
            var container = StyleDataRows.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (container != null && container.ActualHeight > 0)
                return container.ActualHeight;
        }
        return 30.0;
    }

    private void OnTableLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateStickyHeader();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 滚动事件：根据 MText 标题在视口中的位置切换粘性标题。
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateStickyHeader();
    }

    /// <summary>
    /// 根据当前滚动位置决定显示哪组粘性标题。
    /// <para>
    /// 向下滚动：MText 标题完全滚出视口顶部（Y≤0）时切换为 MText 粘性标题，
    /// 实现无缝过渡（用户看到 MText 标题自然向上移动并被覆盖层接管）。
    /// </para>
    /// <para>
    /// 向上滚动：MText 标题下移到覆盖层下方（Y&gt;0）时切换回样式表粘性标题，
    /// 确保样式表最后一行数据可见。
    /// </para>
    /// </summary>
    private void UpdateStickyHeader()
    {
        var vm = ViewModel;
        bool hasStyle = vm.HasItems;
        bool hasMText = vm.HasInlineFix;

        if (!hasStyle && !hasMText) return;

        // 仅样式表：始终显示样式表标题
        if (hasStyle && !hasMText)
        {
            StickyStyleHeader.Visibility = Visibility.Visible;
            StickyMTextHeader.Visibility = Visibility.Collapsed;
            return;
        }

        // 仅 MText：始终显示 MText 标题
        if (!hasStyle && hasMText)
        {
            StickyStyleHeader.Visibility = Visibility.Collapsed;
            StickyMTextHeader.Visibility = Visibility.Visible;
            return;
        }

        // 两者都有：根据 MText 标题位置切换
        if (!MTextHeaderMarker.IsLoaded || !ContentScroll.IsLoaded)
        {
            StickyStyleHeader.Visibility = Visibility.Visible;
            StickyMTextHeader.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            // MText 标题相对于 ScrollViewer 视口顶部的 Y 坐标
            var mtextPos = MTextHeaderMarker.TransformToAncestor(ContentScroll).Transform(new Point(0, 0));

            if (mtextPos.Y <= 0)
            {
                // MText 标题已完全滚出视口顶部 → 切换为 MText 粘性标题
                StickyStyleHeader.Visibility = Visibility.Collapsed;
                StickyMTextHeader.Visibility = Visibility.Visible;
            }
            else
            {
                // MText 标题仍在视口内或下方 → 显示样式表粘性标题
                StickyStyleHeader.Visibility = Visibility.Visible;
                StickyMTextHeader.Visibility = Visibility.Collapsed;
            }
        }
        catch (InvalidOperationException)
        {
            // TransformToAncestor 在元素不在可视树中时抛出异常
            StickyStyleHeader.Visibility = Visibility.Visible;
            StickyMTextHeader.Visibility = Visibility.Collapsed;
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
