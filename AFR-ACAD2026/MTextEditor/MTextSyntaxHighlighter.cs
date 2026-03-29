using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 格式代码语法高亮器。
/// 识别并高亮 MText Contents 中的格式控制代码。
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

    // 格式组开头 {\C1; {\H2x; {\fArial; 等
    private static readonly SolidColorBrush FormatCodeBrush = CreateFrozenBrush(0, 120, 212);
    // 转义序列 \P \~ \\ 等
    private static readonly SolidColorBrush EscapeBrush = CreateFrozenBrush(16, 136, 68);
    // 格式组结尾 }
    private static readonly SolidColorBrush BraceBrush = CreateFrozenBrush(212, 118, 10);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 根据 MText 原始内容创建带语法高亮的 FlowDocument。
    /// </summary>
    public static FlowDocument CreateHighlightedDocument(string contents)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            PagePadding = new Thickness(0)
        };

        if (string.IsNullOrEmpty(contents))
        {
            doc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
            return doc;
        }

        var lines = contents.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            HighlightLine(paragraph, line);
            doc.Blocks.Add(paragraph);
        }

        return doc;
    }

    private static void HighlightLine(Paragraph paragraph, string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        int lastIndex = 0;
        var matches = FormatCodeRegex().Matches(line);

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                paragraph.Inlines.Add(new Run(line[lastIndex..match.Index]));
            }

            var run = new Run(match.Value);
            if (match.Value == "}")
            {
                run.Foreground = BraceBrush;
                run.FontWeight = FontWeights.SemiBold;
            }
            else if (match.Value.StartsWith("{\\"))
            {
                run.Foreground = FormatCodeBrush;
                run.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                run.Foreground = EscapeBrush;
                run.FontWeight = FontWeights.SemiBold;
            }

            paragraph.Inlines.Add(run);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < line.Length)
        {
            paragraph.Inlines.Add(new Run(line[lastIndex..]));
        }
    }
}
