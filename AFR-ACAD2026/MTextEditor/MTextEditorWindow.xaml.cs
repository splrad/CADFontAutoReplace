using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 编辑器窗口。
/// 左侧 RichTextBox 高亮显示格式代码，右侧 TextBox 显示原始内容。
/// 两侧实时同步，编辑任一侧均可。
/// </summary>
public partial class MTextEditorWindow : Window
{
    private readonly MTextEditorViewModel _viewModel;
    private bool _isSyncing;
    private readonly DispatcherTimer _highlightTimer;

    internal MTextEditorWindow(MTextEditorViewModel vm)
    {
        _viewModel = vm;
        DataContext = vm;
        InitializeComponent();

        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _highlightTimer.Tick += OnHighlightTimerTick;

        Loaded += (_, _) => SyncFromViewModel();
    }

    /// <summary>
    /// 从 ViewModel 同步内容到两侧编辑器。
    /// </summary>
    private void SyncFromViewModel()
    {
        _isSyncing = true;
        try
        {
            RawEditor.Text = _viewModel.RawContents;
            VisualEditor.Document = MTextSyntaxHighlighter.CreateHighlightedDocument(_viewModel.RawContents);
        }
        finally { _isSyncing = false; }
    }

    private void VisualEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        try
        {
            string text = new TextRange(
                VisualEditor.Document.ContentStart,
                VisualEditor.Document.ContentEnd).Text.TrimEnd('\r', '\n');
            RawEditor.Text = text;
            _viewModel.RawContents = text;
        }
        finally { _isSyncing = false; }

        // 延迟刷新高亮（避免打字时光标跳动）
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void RawEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        try
        {
            _viewModel.RawContents = RawEditor.Text;
            VisualEditor.Document = MTextSyntaxHighlighter.CreateHighlightedDocument(RawEditor.Text);
        }
        finally { _isSyncing = false; }
    }

    /// <summary>
    /// 延迟刷新可视化编辑器的语法高亮，并尽可能恢复光标位置。
    /// </summary>
    private void OnHighlightTimerTick(object? sender, EventArgs e)
    {
        _highlightTimer.Stop();
        if (!VisualEditor.IsFocused) return;

        _isSyncing = true;
        try
        {
            int offset = VisualEditor.Document.ContentStart
                .GetOffsetToPosition(VisualEditor.CaretPosition);

            VisualEditor.Document = MTextSyntaxHighlighter.CreateHighlightedDocument(_viewModel.RawContents);

            var restored = VisualEditor.Document.ContentStart.GetPositionAtOffset(offset);
            if (restored != null)
                VisualEditor.CaretPosition = restored;
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
