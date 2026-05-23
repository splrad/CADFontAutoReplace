namespace AFR.Models;

/// <summary>
/// Hook 实际命中的运行时字体映射记录。
/// </summary>
/// <param name="Source">字体来源域，例如“样式表”或“MText”。</param>
/// <param name="Owner">来源对象，例如样式名或“多行文字”。</param>
/// <param name="OriginalFont">图纸中的原始字体名。</param>
/// <param name="BaseFont">去除前导 @ 后的基础字体名；无 @ 时为空。</param>
/// <param name="FontType">字体类型。</param>
/// <param name="ReplacementFont">文件级映射目标。</param>
/// <param name="ExecutingHook">实际执行映射的 Hook。</param>
/// <param name="Result">处理结果。</param>
public sealed record RuntimeFontMappingResultRecord(
    string Source,
    string Owner,
    string OriginalFont,
    string BaseFont,
    string FontType,
    string ReplacementFont,
    string ExecutingHook,
    string Result);
