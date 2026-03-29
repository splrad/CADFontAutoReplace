using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 编辑器窗口。
/// 左侧 RichTextBox 编辑原始代码（语法高亮 + \P 自动换行），
/// 右侧 RichTextBox 只读预览效果。
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
    /// 从 ViewModel 同步到两侧面板（初始化 / 批量替换后调用）。
    /// </summary>
    private void SyncFromViewModel()
    {
        _isSyncing = true;
        try
        {
            string displayText = MTextEditorViewModel.ToDisplayFormat(_viewModel.RawContents);
            RawEditor.Document = MTextSyntaxHighlighter.CreateHighlightedRawDocument(displayText);
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
            string displayText = new TextRange(
                RawEditor.Document.ContentStart,
                RawEditor.Document.ContentEnd).Text.TrimEnd('\r', '\n');
            _viewModel.RawContents = MTextEditorViewModel.ToRawFormat(displayText);
            PreviewEditor.Document = MTextSyntaxHighlighter.CreatePreviewDocument(_viewModel.RawContents);
        }
        finally { _isSyncing = false; }

        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    /// <summary>
    /// 防抖刷新语法高亮，尽可能恢复光标位置。
    /// </summary>
    private void OnHighlightTimerTick(object? sender, EventArgs e)
    {
        _highlightTimer.Stop();
        if (!RawEditor.IsFocused) return;

        _isSyncing = true;
        try
        {
            int offset = RawEditor.Document.ContentStart
                .GetOffsetToPosition(RawEditor.CaretPosition);

            string displayText = MTextEditorViewModel.ToDisplayFormat(_viewModel.RawContents);
            RawEditor.Document = MTextSyntaxHighlighter.CreateHighlightedRawDocument(displayText);

            var restored = RawEditor.Document.ContentStart.GetPositionAtOffset(offset);
            if (restored != null)
                RawEditor.CaretPosition = restored;
        }
        finally { _isSyncing = false; }
    }

    private void OnBatchReplace(object sender, RoutedEventArgs e)
    {
        _viewModel.BatchReplace();
        SyncFromViewModel();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
