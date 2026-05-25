using System.Collections.Concurrent;
using AFR.Models;

namespace AFR.FontMapping;

/// <summary>
/// 记录当前执行周期内文件级字体 Hook 实际命中的运行时映射。
/// </summary>
internal static class FontRuntimeMappingStore
{
    private static readonly ConcurrentDictionary<string, RuntimeFontMappingResultRecord> RuntimeMappings =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Clear()
    {
        RuntimeMappings.Clear();
    }

    internal static void RecordRuntimeMapping(
        string source,
        string owner,
        string originalFont,
        string baseFont,
        string fontType,
        string replacementFont,
        string executingHook,
        string result)
    {
        if (string.IsNullOrWhiteSpace(originalFont)
            || string.IsNullOrWhiteSpace(replacementFont))
        {
            return;
        }

        string normalizedSource = NormalizeSource(source);
        string normalizedOwner = string.IsNullOrWhiteSpace(owner) ? string.Empty : owner.Trim();
        string hook = string.IsNullOrWhiteSpace(executingHook) ? string.Empty : executingHook.Trim();
        string normalizedResult = string.IsNullOrWhiteSpace(result) ? "已映射" : result.Trim();

        var record = new RuntimeFontMappingResultRecord(
            normalizedSource,
            normalizedOwner,
            FontRedirectResolver.NormalizeInputName(originalFont),
            FontRedirectResolver.NormalizeInputName(baseFont),
            string.IsNullOrWhiteSpace(fontType) ? "未知" : fontType.Trim(),
            FontRedirectResolver.NormalizeInputName(replacementFont),
            hook,
            normalizedResult);

        RuntimeMappings[GetRuntimeKey(record)] = record;
    }

    internal static List<RuntimeFontMappingResultRecord> GetRuntimeMappingResults()
        => RuntimeMappings.Values
            .OrderBy(x => x.Source, StringComparer.Ordinal)
            .ThenBy(x => x.Owner, StringComparer.Ordinal)
            .ThenBy(x => x.OriginalFont, StringComparer.Ordinal)
            .ToList();

    private static string NormalizeSource(string source)
    {
        if (source.StartsWith("MText", StringComparison.OrdinalIgnoreCase))
            return "MText";
        return string.IsNullOrWhiteSpace(source) ? "未知" : source;
    }

    private static string GetFontTypeText(FontRedirectKind kind)
        => kind switch
        {
            FontRedirectKind.TrueType => "TrueType字体",
            FontRedirectKind.ShxBigFont => "SHX大字体",
            _ => "SHX主字体"
        };

    private static string GetRuntimeKey(RuntimeFontMappingResultRecord record)
        => string.Concat(
            record.Source,
            "\u001F",
            record.Owner,
            "\u001F",
            record.OriginalFont,
            "\u001F",
            record.FontType,
            "\u001F",
            record.ExecutingHook);
}
