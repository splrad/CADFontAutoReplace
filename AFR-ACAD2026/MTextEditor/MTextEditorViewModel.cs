namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 查看器的格式转换工具。
/// </summary>
internal static class MTextEditorViewModel
{
    /// <summary>
    /// 将 MText 原始内容转为显示格式（\P 后插入换行以提高可读性）。
    /// </summary>
    internal static string ToDisplayFormat(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Replace("\\P", "\\P\n");
    }
}
