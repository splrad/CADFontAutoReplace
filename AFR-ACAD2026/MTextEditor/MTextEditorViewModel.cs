using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 编辑器的 ViewModel。
/// 管理原始内容、查找替换、状态消息。
/// </summary>
internal sealed class MTextEditorViewModel : INotifyPropertyChanged
{
    private string _rawContents;
    private string _searchText = string.Empty;
    private string _replaceText = string.Empty;
    private string _statusMessage = string.Empty;

    public string RawContents
    {
        get => _rawContents;
        set { if (_rawContents != value) { _rawContents = value; OnPropertyChanged(); } }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (_searchText != value) { _searchText = value ?? string.Empty; OnPropertyChanged(); } }
    }

    public string ReplaceText
    {
        get => _replaceText;
        set { if (_replaceText != value) { _replaceText = value ?? string.Empty; OnPropertyChanged(); } }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    public MTextEditorViewModel(string contents)
    {
        _rawContents = contents ?? string.Empty;
    }

    /// <summary>
    /// 将原始代码复制到系统剪贴板。
    /// </summary>
    public void CopyToClipboard()
    {
        if (!string.IsNullOrEmpty(RawContents))
            System.Windows.Clipboard.SetText(RawContents);
        StatusMessage = "已复制到剪贴板";
    }

    /// <summary>
    /// 批量替换：将 RawContents 中所有 SearchText 替换为 ReplaceText。
    /// </summary>
    public void BatchReplace()
    {
        if (string.IsNullOrEmpty(SearchText))
        {
            StatusMessage = "请输入查找内容";
            return;
        }

        int count = CountOccurrences(RawContents, SearchText);
        if (count > 0)
        {
            RawContents = RawContents.Replace(SearchText, ReplaceText);
            StatusMessage = $"已替换 {count} 处";
        }
        else
        {
            StatusMessage = "未找到匹配内容";
        }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern)) return 0;
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
