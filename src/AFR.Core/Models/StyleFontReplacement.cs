namespace AFR.Models;

/// <summary>
/// 单个文字样式的字体替换规格，描述应该用什么字体来替换缺失的字体。
/// 由用户配置或自动匹配生成，供 FontReplacer 执行实际替换时使用。
/// </summary>
public sealed class StyleFontReplacement
{
    public string StyleName { get; }
    public bool IsTrueType { get; }
    public string MainFontReplacement { get; }
    public string BigFontReplacement { get; }

    public StyleFontReplacement(
        string styleName,
        bool isTrueType,
        string mainFontReplacement,
        string bigFontReplacement)
    {
        StyleName = styleName;
        IsTrueType = isTrueType;
        MainFontReplacement = mainFontReplacement;
        BigFontReplacement = bigFontReplacement;
    }

    /// <summary>创建副本并替换指定属性（等效于 record 的 with 表达式）。</summary>
    public StyleFontReplacement With(
        string? styleName = null,
        bool? isTrueType = null,
        string? mainFontReplacement = null,
        string? bigFontReplacement = null)
    {
        return new StyleFontReplacement(
            styleName ?? StyleName,
            isTrueType ?? IsTrueType,
            mainFontReplacement ?? MainFontReplacement,
            bigFontReplacement ?? BigFontReplacement);
    }
}
