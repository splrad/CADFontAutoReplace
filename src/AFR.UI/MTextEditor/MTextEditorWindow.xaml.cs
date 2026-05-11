using System.Windows;
using System.Windows.Input;

namespace AFR.UI;

/// <summary>
/// MText 查看器窗口。
/// 显示带语法高亮的 MText 原始代码，只读。
/// </summary>
public partial class MTextEditorWindow : Window
{
    private readonly MTextEditorViewModel _viewModel;

    public MTextEditorWindow(string rawContents)
    {
        InitializeComponent();

        _viewModel = new MTextEditorViewModel(rawContents);
        _viewModel.CloseRequested += OnCloseRequested;
        DataContext = _viewModel;
        RawViewer.Document = _viewModel.Document;

        WindowPositionHelper.SetupCenterOnParent(this);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
