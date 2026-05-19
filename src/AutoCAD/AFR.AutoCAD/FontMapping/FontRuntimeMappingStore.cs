using System.Collections.Concurrent;
using AFR.Models;

namespace AFR.FontMapping;

/// <summary>
/// 记录当前执行周期内两个字体 Hook 实际命中的运行时映射。
/// </summary>
internal static class FontRuntimeMappingStore
{
    private static readonly ConcurrentDictionary<string, RuntimeFontMappingRecord> StyleMappings =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, InlineFontFixRecord> InlineMappings =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Clear()
    {
        ClearStyleMappings();
        ClearInlineMappings();
    }

    internal static void ClearStyleMappings() => StyleMappings.Clear();

    internal static void ClearInlineMappings() => InlineMappings.Clear();

    internal static void RecordStyleMapping(RuntimeFontMappingRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.StyleName)
            || string.IsNullOrWhiteSpace(record.OriginalFont)
            || string.IsNullOrWhiteSpace(record.ReplacementFont))
        {
            return;
        }

        StyleMappings[GetStyleKey(record)] = record;
    }

    internal static void RecordInlineMapping(
        string originalFont,
        string replacementFont,
        InlineFontType inlineType)
    {
        if (string.IsNullOrWhiteSpace(originalFont) || string.IsNullOrWhiteSpace(replacementFont))
            return;

        string category = inlineType switch
        {
            InlineFontType.ShxBigFont => "SHX大字体",
            InlineFontType.TrueType => "TrueType映射",
            _ => "SHX主字体"
        };

        string normalizedOriginal = FontRedirectResolver.NormalizeInputName(originalFont);
        if (string.IsNullOrWhiteSpace(normalizedOriginal))
            return;

        var record = new InlineFontFixRecord(
            normalizedOriginal,
            FontRedirectResolver.NormalizeInputName(replacementFont),
            "MText内联",
            category);

        InlineMappings[GetInlineKey(record)] = record;
    }

    internal static List<RuntimeFontMappingRecord> GetStyleMappings()
        => StyleMappings.Values
            .OrderBy(x => x.StyleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.OriginalFont, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static List<InlineFontFixRecord> GetInlineMappings()
        => InlineMappings.Values
            .OrderBy(x => x.MissingFont, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ReplacementFont, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string GetStyleKey(RuntimeFontMappingRecord record)
        => string.Concat(record.StyleName, "\u001F", record.OriginalFont, "\u001F", record.MappingCategory);

    private static string GetInlineKey(InlineFontFixRecord record)
        => string.Concat(record.MissingFont, "\u001F", record.ReplacementFont, "\u001F", record.FontCategory);
}
