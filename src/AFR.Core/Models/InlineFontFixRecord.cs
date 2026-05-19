namespace AFR.Models;

/// <summary>
/// 单条 MText（多行文字）内联字体映射的修复记录。
/// 当 MText 内容中通过 \F 或 \f 引用了缺失字体时，记录修复的详细信息。
/// </summary>
/// <param name="MissingFont">缺失的原始字体名称。</param>
/// <param name="ReplacementFont">用于替换的目标字体名称。</param>
/// <param name="FixMethod">修复方式的描述（如 "SHX映射"、"TrueType映射"）。</param>
/// <param name="FontCategory">字体分类（如 "SHX主字体"、"SHX大字体"、"TrueType映射"），用于统计和日志输出。</param>
public sealed record InlineFontFixRecord(
    string MissingFont,
    string ReplacementFont,
    string FixMethod,
    string FontCategory);
