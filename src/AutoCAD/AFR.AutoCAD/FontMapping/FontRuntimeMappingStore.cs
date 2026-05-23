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
        FontRuntimeRequest request,
        string executingHook,
        string result)
    {
        if (string.IsNullOrWhiteSpace(request.OriginalDisplayFont)
            || string.IsNullOrWhiteSpace(request.ReplacementFont))
        {
            return;
        }

        string source = NormalizeSource(request.Source);
        string owner = !string.IsNullOrWhiteSpace(request.Owner)
            ? request.Owner
            : source == "样式表" ? string.Empty : "多行文字";
        string hook = string.IsNullOrWhiteSpace(executingHook) ? request.ExecutingHook : executingHook;
        string normalizedResult = string.IsNullOrWhiteSpace(result) ? "已映射" : result;

        var record = new RuntimeFontMappingResultRecord(
            source,
            owner,
            FontRedirectResolver.NormalizeInputName(request.OriginalDisplayFont),
            request.BaseFont,
            GetFontTypeText(request.Kind),
            FontRedirectResolver.NormalizeInputName(request.ReplacementFont),
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
        if (source.StartsWith("StyleTextStyleHook", StringComparison.OrdinalIgnoreCase))
            return "样式表";
        if (source.StartsWith("MText", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("AcGiTextStyle", StringComparison.OrdinalIgnoreCase))
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
