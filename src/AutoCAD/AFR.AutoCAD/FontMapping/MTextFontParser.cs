using System.IO;

namespace AFR.FontMapping;

/// <summary>
/// MText 内联字体引用的类型。
/// </summary>
internal enum InlineFontType
{
    /// <summary>SHX 主字体（\F 大写，逗号前部分）。</summary>
    ShxMain,

    /// <summary>SHX 大字体（\F 大写，逗号后部分）。</summary>
    ShxBigFont,

    /// <summary>TrueType 字族名（\f 小写）。</summary>
    TrueType
}

/// <summary>
/// MText Contents 字符串的流式解析器。
/// 提取 \F（SHX）和 \f（TrueType）格式代码中的内联字体引用。
///
/// 格式规范（大小写敏感）：
///   \Fmain|              → SHX 主字体
///   \Fmain,big|          → SHX 主字体 + 大字体
///   \Fmain,@big|         → SHX 主字体 + 东亚大字体
///   \fName|              → TrueType（简写）
///   \fName;              → TrueType（分号结束）
///   \fName|b0|i0|c134|p2;→ TrueType（完整参数）
///   \F| / \f|            → 继承样式（空字体，跳过）
///
/// 容错：缺少终止符时跳到下一个 '\' 或字符串末尾。
/// </summary>
internal static class MTextFontParser
{
    /// <summary>
    /// 从 MText Contents 字符串中提取所有内联字体引用。
    /// 结果去重，key 为归一化后的字体名（SHX 加 .shx 后缀，TrueType 保留原名）。
    /// </summary>
    internal static void ParseInlineFonts(string? contents, Dictionary<string, InlineFontType> result)
    {
        if (string.IsNullOrEmpty(contents)) return;

        int len = contents.Length;
        int i = 0;

        while (i < len - 1)
        {
            if (contents[i] != '\\')
            {
                i++;
                continue;
            }

            char code = contents[i + 1];

            if (code == '\\')
            {
                // 转义反斜杠 \\ → 跳过两个字符
                i += 2;
                continue;
            }

            if (code == 'F')
            {
                // \F → SHX 字体
                i += 2;
                ParseShxFont(contents, ref i, result);
            }
            else if (code == 'f')
            {
                // \f → TrueType 字体
                i += 2;
                ParseTrueTypeFont(contents, ref i, result);
            }
            else
            {
                // 其它转义序列（\P \O \L \U \~ 等）→ 跳过
                i += 2;
            }
        }
    }

    /// <summary>
    /// 解析 \F 格式代码（SHX 字体）。
    /// 格式: \Fmain[,[@]big]|
    /// 进入时 i 指向 \F 之后的第一个字符。
    /// </summary>
    private static void ParseShxFont(string text, ref int i, Dictionary<string, InlineFontType> result)
    {
        int len = text.Length;

        // 查找终止符 |
        int end = FindTerminator(text, i, '|');

        // 提取 \F 到 | 之间的内容
        string segment = text.Substring(i, end - i);
        i = end < len ? end + 1 : end;

        if (string.IsNullOrWhiteSpace(segment)) return;

        // 按逗号拆分: main[,big]
        int commaPos = segment.IndexOf(',');
        if (commaPos < 0)
        {
            // 仅主字体
            string mainFont = segment.Trim();
            if (mainFont.Length > 0)
                AddShxFont(mainFont, InlineFontType.ShxMain, result);
        }
        else
        {
            // 主字体 + 大字体
            string mainFont = segment.Substring(0, commaPos).Trim();
            string bigFont = segment.Substring(commaPos + 1).Trim();

            if (mainFont.Length > 0)
                AddShxFont(mainFont, InlineFontType.ShxMain, result);
            if (bigFont.Length > 0)
                AddShxFont(bigFont, InlineFontType.ShxBigFont, result);
        }
    }

    /// <summary>
    /// 解析 \f 格式代码（TrueType 字体）。
    /// 格式: \fName[|bN|iN|cN|pN];  或  \fName|  或  \fName;
    /// 进入时 i 指向 \f 之后的第一个字符。
    ///
    /// 歧义处理: 遇到 | 时 look-ahead 判断是参数分隔符还是结束符。
    ///   | 后紧跟 [bicp] + 数字 → 参数分隔符，继续读取到 ;
    ///   否则 → 结束符，字族名结束
    /// </summary>
    private static void ParseTrueTypeFont(string text, ref int i, Dictionary<string, InlineFontType> result)
    {
        int len = text.Length;
        int nameStart = i;

        // 读取字族名，到第一个 | 或 ; 为止
        while (i < len)
        {
            char c = text[i];

            if (c == ';')
            {
                // 分号 → 确定的结束符
                string fontName = text.Substring(nameStart, i - nameStart).Trim();
                i++; // 跳过 ;
                if (fontName.Length > 0)
                    if (!result.ContainsKey(fontName)) result.Add(fontName, InlineFontType.TrueType);
                return;
            }

            if (c == '|')
            {
                // | → 可能是结束符，也可能是参数分隔符
                string fontName = text.Substring(nameStart, i - nameStart).Trim();
                i++; // 跳过 |

                if (IsParameterStart(text, i))
                {
                    // 参数分隔符 → 跳过剩余参数到 ;
                    SkipToSemicolon(text, ref i);
                }

                // 无论哪种情况，字族名已确定
                if (fontName.Length > 0)
                    if (!result.ContainsKey(fontName)) result.Add(fontName, InlineFontType.TrueType);
                return;
            }

            if (c == '\\')
            {
                // 遇到新的转义序列 → 缺少终止符，容错截断
                string fontName = text.Substring(nameStart, i - nameStart).Trim();
                if (fontName.Length > 0)
                    if (!result.ContainsKey(fontName)) result.Add(fontName, InlineFontType.TrueType);
                return;
            }

            i++;
        }

        // 到达字符串末尾，缺少终止符 → 容错
        string remaining = text.Substring(nameStart).Trim();
        if (remaining.Length > 0)
            if (!result.ContainsKey(remaining)) result.Add(remaining, InlineFontType.TrueType);
    }

    #region 辅助方法

    /// <summary>
    /// 查找终止符位置。若找不到则返回字符串末尾或下一个 '\' 的位置（容错）。
    /// </summary>
    private static int FindTerminator(string text, int start, char terminator)
    {
        int len = text.Length;
        for (int j = start; j < len; j++)
        {
            if (text[j] == terminator) return j;
            if (text[j] == '\\') return j; // 遇到新转义 → 容错截断
        }
        return len;
    }

    /// <summary>
    /// 判断当前位置是否为 TrueType 参数起始（b/i/c/p + 数字）。
    /// </summary>
    private static bool IsParameterStart(string text, int pos)
    {
        if (pos >= text.Length) return false;

        char c = text[pos];
        if (c != 'b' && c != 'i' && c != 'c' && c != 'p') return false;

        // 参数字母后必须紧跟数字
        int next = pos + 1;
        return next < text.Length && char.IsDigit(text[next]);
    }

    /// <summary>
    /// 跳过 TrueType 参数序列 |bN|iN|cN|pN 直到遇到 ; 或字符串末尾。
    /// </summary>
    private static void SkipToSemicolon(string text, ref int i)
    {
        int len = text.Length;
        while (i < len)
        {
            if (text[i] == ';')
            {
                i++; // 跳过 ;
                return;
            }
            if (text[i] == '\\')
            {
                // 遇到新转义 → 参数序列异常截断（容错）
                return;
            }
            i++;
        }
    }

    /// <summary>
    /// 添加 SHX 字体到结果集。归一化处理：剥离目录路径 + 确保 .shx 后缀。
    /// </summary>
    private static void AddShxFont(string fontName, InlineFontType fontType, Dictionary<string, InlineFontType> result)
    {
        // MText \F 格式代码可能存储完整路径（如旧版 CAD 安装目录），
        // 剥离路径确保与 Hook 重定向日志和 _availableFonts 的纯文件名 key 对齐
        fontName = Path.GetFileName(fontName);

        string normalized = fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            ? fontName
            : fontName + ".shx";

        if (!result.ContainsKey(normalized)) result.Add(normalized, fontType);
    }

    #endregion
}
