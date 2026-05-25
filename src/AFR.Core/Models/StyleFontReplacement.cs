namespace AFR.Models;

/// <summary>
/// 单个文字样式的字体替换规格，描述应该用什么字体来替换缺失的字体。
/// 由用户配置或自动匹配生成，供 FontReplacer 执行实际替换时使用。
/// </summary>
/// <param name="StyleName">要替换的文字样式名称。</param>
/// <param name="IsTrueType">是否为 TrueType 字体样式（决定替换策略和可选字体范围）。</param>
/// <param name="MainFontReplacement">主字体的替换目标（空字符串表示不替换主字体）。</param>
/// <param name="BigFontReplacement">大字体的替换目标（仅 SHX 样式有效，空字符串表示不替换）。</param>
/// <param name="PreserveTrueTypeAtPrefix">TrueType 样式写回时是否保留 @ 前缀。</param>
public sealed record StyleFontReplacement(
    string StyleName,
    bool IsTrueType,
    string MainFontReplacement,
    string BigFontReplacement,
    bool PreserveTrueTypeAtPrefix = false);
