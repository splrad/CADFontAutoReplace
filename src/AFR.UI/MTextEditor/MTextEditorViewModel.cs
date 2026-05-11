using System;
using System.Windows.Documents;
using System.Windows.Input;

namespace AFR.UI;

/// <summary>
/// MText 格式代码查看器的格式转换工具。
/// 将 MText 原始内容转换为更适合阅读的显示格式。
/// </summary>
internal sealed class MTextEditorViewModel
{
    public FlowDocument Document { get; }

    public ICommand CloseCommand { get; }

    public event EventHandler? CloseRequested;

    public MTextEditorViewModel(string rawContents)
    {
        Document = MTextSyntaxHighlighter.CreateHighlightedRawDocument(ToDisplayFormat(rawContents));
        CloseCommand = new UiRelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// 将 MText 原始内容转为显示格式。
    /// 在每个 \P（段落标记）后插入换行符，使长段落在查看器中分行显示。
    /// </summary>
    private static string ToDisplayFormat(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Replace("\\P", "\\P\n");
    }
}
