namespace AFR.Models;

/// <summary>
/// 单个文字样式的字体可用性检查结果。
/// 记录该样式引用了哪些字体文件，以及这些字体在当前环境中是否缺失。
/// </summary>
public sealed class FontCheckResult
{
    public string StyleName { get; }
    public string FileName { get; }
    public string BigFontFileName { get; }
    public bool IsMainFontMissing { get; }
    public bool IsBigFontMissing { get; }
    public bool IsTrueType { get; }
    public string TypeFace { get; }

    public FontCheckResult(
        string styleName,
        string fileName,
        string bigFontFileName,
        bool isMainFontMissing,
        bool isBigFontMissing,
        bool isTrueType,
        string typeFace)
    {
        StyleName = styleName;
        FileName = fileName;
        BigFontFileName = bigFontFileName;
        IsMainFontMissing = isMainFontMissing;
        IsBigFontMissing = isBigFontMissing;
        IsTrueType = isTrueType;
        TypeFace = typeFace;
    }
}
