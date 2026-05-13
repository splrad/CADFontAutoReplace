using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AFR.Models;

namespace AFR.UI;

/// <summary>
/// 字体替换日志窗口。
/// 显示缺失字体检测结果，支持手动逐一指定替换字体。
/// 通过 ApplyReplacementsHandler 回调解耦 CAD 平台操作。
/// </summary>
public partial class FontReplacementLogWindow : Window
{
    private bool _comboBoxDropDownOpen;
    private Func<IReadOnlyList<StyleFontReplacement>, int>? _applyReplacementsHandler;
    private Func<FontReplacementLogViewModel>? _refreshHandler;

    public FontReplacementLogViewModel ViewModel { get; private set; }

    /// <summary>累计成功替换的样式数量。</summary>
    public int AppliedCount => ViewModel.AppliedCount;

    /// <summary>最近一次应用替换时的替换指令列表，供调用方生成统计日志。</summary>
    public IReadOnlyList<StyleFontReplacement>? LastAppliedReplacements => ViewModel.LastAppliedReplacements;

    /// <summary>
    /// 替换操作回调。接收替换列表，返回成功替换的数量。
    /// 由调用方提供平台特定的实现（如 AutoCAD 的文档锁定与字体替换）。
    /// </summary>
    public Func<IReadOnlyList<StyleFontReplacement>, int>? ApplyReplacementsHandler
    {
        get => _applyReplacementsHandler;
        set
        {
            _applyReplacementsHandler = value;
            ViewModel.ApplyReplacementsHandler = value;
        }
    }

    /// <summary>
    /// 刷新回调。应用替换成功后调用，返回新的 ViewModel 以刷新界面。
    /// 由调用方提供平台特定的实现（如重新检测缺失字体并构建新 ViewModel）。
    /// </summary>
    public Func<FontReplacementLogViewModel>? RefreshHandler
    {
        get => _refreshHandler;
        set
        {
            _refreshHandler = value;
            ViewModel.RefreshHandler = value;
        }
    }

    public FontReplacementLogWindow(FontReplacementLogViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        AttachViewModel(vm);
        WindowPositionHelper.SetupCenterOnParent(this);

        // 确保窗口不超过屏幕可用高度
        double workArea = SystemParameters.WorkArea.Height;
        if (Height > workArea)
            Height = workArea;
    }

    private void AttachViewModel(FontReplacementLogViewModel viewModel)
    {
        if (!ReferenceEquals(ViewModel, viewModel))
            DetachViewModel(ViewModel);

        ViewModel = viewModel;
        ViewModel.ApplyReplacementsHandler = _applyReplacementsHandler;
        ViewModel.RefreshHandler = _refreshHandler;
        ViewModel.ViewModelRefreshed += OnViewModelRefreshed;
        ViewModel.CloseRequested += OnCloseRequested;
        DataContext = ViewModel;
    }

    private void DetachViewModel(FontReplacementLogViewModel viewModel)
    {
        viewModel.ViewModelRefreshed -= OnViewModelRefreshed;
        viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnViewModelRefreshed(object? sender, FontReplacementLogViewModel viewModel)
    {
        AttachViewModel(viewModel);
        Dispatcher.BeginInvoke(new Action(UpdateStickyHeader), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
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
        bool hasRuntime = vm.HasRuntimeMapping;
        bool hasMText = vm.HasInlineFix;

        if (!hasStyle && !hasRuntime && !hasMText) return;

        // 仅样式表：始终显示样式表标题
        if (hasStyle && !hasRuntime && !hasMText)
        {
            ShowStickyHeader(style: true, runtime: false, mtext: false);
            return;
        }

        // 仅运行时映射：始终显示运行时映射标题
        if (!hasStyle && hasRuntime && !hasMText)
        {
            ShowStickyHeader(style: false, runtime: true, mtext: false);
            return;
        }

        // 仅 MText：始终显示 MText 标题
        if (!hasStyle && !hasRuntime && hasMText)
        {
            ShowStickyHeader(style: false, runtime: false, mtext: true);
            return;
        }

        // 多个区块：根据各区块标题位置切换
        if (!ContentScroll.IsLoaded)
        {
            ShowFirstAvailableHeader(hasStyle, hasRuntime, hasMText);
            return;
        }

        try
        {
            if (hasMText && MTextHeaderMarker.IsLoaded)
            {
                var mtextPos = MTextHeaderMarker.TransformToAncestor(ContentScroll).Transform(new Point(0, 0));
                if (mtextPos.Y <= 0)
                {
                    ShowStickyHeader(style: false, runtime: false, mtext: true);
                    return;
                }
            }

            if (hasRuntime && RuntimeMappingHeaderMarker.IsLoaded)
            {
                var runtimePos = RuntimeMappingHeaderMarker.TransformToAncestor(ContentScroll).Transform(new Point(0, 0));
                if (runtimePos.Y <= 0)
                {
                    ShowStickyHeader(style: false, runtime: true, mtext: false);
                    return;
                }
            }

            ShowFirstAvailableHeader(hasStyle, hasRuntime, hasMText);
        }
        catch (InvalidOperationException)
        {
            // TransformToAncestor 在元素不在可视树中时抛出异常
            ShowFirstAvailableHeader(hasStyle, hasRuntime, hasMText);
        }
    }

    private void ShowFirstAvailableHeader(bool hasStyle, bool hasRuntime, bool hasMText)
    {
        if (hasStyle)
        {
            ShowStickyHeader(style: true, runtime: false, mtext: false);
            return;
        }

        if (hasRuntime)
        {
            ShowStickyHeader(style: false, runtime: true, mtext: false);
            return;
        }

        if (hasMText)
            ShowStickyHeader(style: false, runtime: false, mtext: true);
    }

    private void ShowStickyHeader(bool style, bool runtime, bool mtext)
    {
        StickyStyleHeader.Visibility = style ? Visibility.Visible : Visibility.Collapsed;
        StickyRuntimeHeader.Visibility = runtime ? Visibility.Visible : Visibility.Collapsed;
        StickyMTextHeader.Visibility = mtext ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
