using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 编辑器窗口。
/// 左侧 TextBox 编辑原始代码（\P 自动换行显示，支持撤回），
/// 右侧 RichTextBox 只读预览效果。
/// </summary>
public partial class MTextEditorWindow : Window
{
    private readonly MTextEditorViewModel _viewModel;
    private bool _isSyncing;

    internal MTextEditorWindow(MTextEditorViewModel vm)
    {
        _viewModel = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => SyncFromViewModel();
    }

    /// <summary>
    /// 从 ViewModel 同步到两侧面板。
    /// 注意：设置 TextBox.Text 会清空撤回历史（仅在初始化和批量替换时调用）。
    /// </summary>
    private void SyncFromViewModel()
    {
        _isSyncing = true;
        try
        {
            RawEditor.Text = MTextEditorViewModel.ToDisplayFormat(_viewModel.RawContents);
            PreviewEditor.Document = MTextSyntaxHighlighter.CreatePreviewDocument(_viewModel.RawContents);
        }
        finally { _isSyncing = false; }
    }

    private void RawEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        try
        {
            _viewModel.RawContents = MTextEditorViewModel.ToRawFormat(RawEditor.Text);
            PreviewEditor.Document = MTextSyntaxHighlighter.CreatePreviewDocument(_viewModel.RawContents);
        }
        finally { _isSyncing = false; }
    }

    private void OnBatchReplace(object sender, RoutedEventArgs e)
    {
        _viewModel.BatchReplace();
        SyncFromViewModel();
    }

    private void OnCopyCode(object sender, RoutedEventArgs e) => _viewModel.CopyToClipboard();
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
