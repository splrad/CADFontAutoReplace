namespace AFR.Models;

/// <summary>
/// 单条 MText（多行文字）内联字体映射的修复记录。
/// 当 MText 内容中通过 \F 或 \f 引用了缺失字体时，记录修复的详细信息。
/// </summary>
public sealed class InlineFontFixRecord
{
    public string MissingFont { get; }
    public string ReplacementFont { get; }
    public string FixMethod { get; }
    public string FontCategory { get; }

    public InlineFontFixRecord(
        string missingFont,
        string replacementFont,
        string fixMethod,
        string fontCategory)
    {
        MissingFont = missingFont;
        ReplacementFont = replacementFont;
        FixMethod = fixMethod;
        FontCategory = fontCategory;
    }
}
