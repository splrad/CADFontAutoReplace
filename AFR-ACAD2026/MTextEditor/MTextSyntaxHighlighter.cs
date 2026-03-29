using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 格式代码解析器 — 将原始 Contents 转换为可视预览文档。
/// 去除格式代码，\P 转为段落分隔，转义序列还原为可读字符。
/// </summary>
internal static partial class MTextSyntaxHighlighter
{
    /// <summary>
    /// 匹配 MText 格式控制代码：
    /// {\Xparams; — 格式组开头（颜色、字高、字体等）
    /// }           — 格式组结尾
    /// \P \~ \\ \{ \} 等 — 转义序列
    /// \S...;      — 堆叠/分数
    /// </summary>
    [GeneratedRegex(@"\{\\[A-Za-z][^;]*;|\}|\\[PpOoLlUu~\\{}]|\\S[^;]*;")]
    internal static partial Regex FormatCodeRegex();

    /// <summary>
    /// 根据 MText 原始内容创建预览文档。
    /// 去除格式控制代码，\P 转为段落分隔，转义序列还原。
    /// </summary>
    public static FlowDocument CreatePreviewDocument(string rawContents)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(0)
        };

        if (string.IsNullOrEmpty(rawContents))
        {
            doc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
            return doc;
        }

        // 将格式代码替换为对应的可视文本
        string processed = FormatCodeRegex().Replace(rawContents, EvaluateFormatCode);

        // 按换行符（来自 \P 转换）分段
        var lines = processed.Split('\n');
        foreach (var line in lines)
        {
            var paragraph = new Paragraph(new Run(line))
            {
                Margin = new Thickness(0, 0, 0, 2)
            };
            doc.Blocks.Add(paragraph);
        }

        return doc;
    }

    /// <summary>
    /// 将匹配到的格式代码转换为预览用的可读文本。
    /// </summary>
    private static string EvaluateFormatCode(Match match)
    {
        string v = match.Value;

        // 段落分隔
        if (v is "\\P") return "\n";

        // 转义序列还原
        if (v is "\\\\") return "\\";
        if (v is "\\~") return "\u00A0";
        if (v is "\\{") return "{";
        if (v is "\\}") return "}";

        // 格式组结尾 — 去除
        if (v is "}") return "";

        // 格式组开头 {\C1; {\H2x; {\fArial; 等 — 去除
        if (v.StartsWith("{\\")) return "";

        // 堆叠 \S1/2; → 显示为 1/2
        if (v.StartsWith("\\S") && v.EndsWith(";") && v.Length > 3)
            return v[2..^1];

        // 其他控制代码 — 去除
        return "";
    }
}
