namespace AFR.Models;

/// <summary>
/// 运行时字体映射记录。
/// 用于展示不写回样式表、仅由 Hook 在显示阶段兜底处理的字体映射。
/// </summary>
/// <param name="StyleName">文字样式名称。</param>
/// <param name="OriginalFont">图纸中保留的原始字体名。</param>
/// <param name="ReplacementFont">运行时映射目标。</param>
/// <param name="MappingCategory">映射类别。</param>
/// <param name="Status">映射状态说明。</param>
public sealed record RuntimeFontMappingRecord(
    string StyleName,
    string OriginalFont,
    string ReplacementFont,
    string MappingCategory,
    string Status);
