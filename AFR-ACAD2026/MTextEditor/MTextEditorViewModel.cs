using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 编辑器的 ViewModel。
/// 管理原始内容、查找替换、显示格式转换。
/// </summary>
internal sealed class MTextEditorViewModel : INotifyPropertyChanged
{
    private string _rawContents;
    private string _searchText = string.Empty;
    private string _replaceText = string.Empty;
    private string _statusMessage = string.Empty;

    /// <summary>
    /// MText 原始内容（实际存储格式，\P 为段落分隔符）。
    /// </summary>
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
    /// 将 MText 原始内容转为编辑器显示格式（\P 后插入换行以提高可读性）。
    /// </summary>
    internal static string ToDisplayFormat(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Replace("\\P", "\\P\n");
    }

    /// <summary>
    /// 将编辑器显示格式转回 MText 原始内容。
    /// 先还原 \P 后的显示换行，再将用户手动输入的换行转为 \P。
    /// </summary>
    internal static string ToRawFormat(string display)
    {
        if (string.IsNullOrEmpty(display)) return string.Empty;
        string result = display.Replace("\\P\r\n", "\\P").Replace("\\P\n", "\\P");
        result = result.Replace("\r\n", "\\P").Replace("\n", "\\P");
        return result;
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
