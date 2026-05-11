#if DEBUG

using System.Windows;
using System.Windows.Input;

namespace AFR.UI;

/// <summary>
/// MText 插入器窗口。
/// 提供预设模板选择和自定义格式代码输入两种模式，返回用户确认的 MText 内容。
/// </summary>
public partial class MTextInsertWindow : Window
{
    private readonly MTextInsertViewModel _viewModel;

    /// <summary>用户确认的 MText 内容。为 null 表示取消。</summary>
    public string? ResultContents => _viewModel.ResultContents;

    /// <summary>
    /// 初始化 MText 插入器窗口。
    /// </summary>
    public MTextInsertWindow()
    {
        InitializeComponent();

        _viewModel = new MTextInsertViewModel();
        _viewModel.CloseRequested += OnCloseRequested;
        _viewModel.MessageRequested += OnMessageRequested;
        DataContext = _viewModel;

        WindowPositionHelper.SetupCenterOnParent(this);
    }

    private void OnCloseRequested(object? sender, UiDialogCloseRequestedEventArgs e)
    {
        if (e.DialogResult.HasValue)
            DialogResult = e.DialogResult;
        else
            Close();
    }

    private static void OnMessageRequested(object? sender, MTextInsertMessageRequestedEventArgs e)
    {
        HandyControl.Controls.MessageBox.Show(e.Message, e.Title, MessageBoxButton.OK, e.Image);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}

#endif
