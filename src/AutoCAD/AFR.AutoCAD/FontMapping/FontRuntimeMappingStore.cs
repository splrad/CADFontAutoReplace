using System.Collections.Concurrent;
using AFR.Models;

namespace AFR.FontMapping;

/// <summary>
/// 记录当前执行周期内文件级字体 Hook 实际命中的运行时映射。
/// 只有 HookHandler 真实 redirect 或失败记录会进入这里，注册和候选不算成功映射。
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

    internal static void RecordFailedRuntimeMapping(
        string source,
        string owner,
        string originalFont,
        string baseFont,
        string fontType,
        string executingHook,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(originalFont))
            return;

        string normalizedSource = NormalizeSource(source);
        string normalizedOwner = string.IsNullOrWhiteSpace(owner) ? string.Empty : owner.Trim();
        string hook = string.IsNullOrWhiteSpace(executingHook) ? string.Empty : executingHook.Trim();
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "无法解析替换字体" : reason.Trim();

        var record = new RuntimeFontMappingResultRecord(
            normalizedSource,
            normalizedOwner,
            FontRedirectResolver.NormalizeInputName(originalFont),
            FontRedirectResolver.NormalizeInputName(baseFont),
            string.IsNullOrWhiteSpace(fontType) ? "未知" : fontType.Trim(),
            string.Empty,
            hook,
            "映射失败：" + normalizedReason);

        RuntimeMappings.AddOrUpdate(
            GetRuntimeKey(record),
            record,
            (_, existing) => IsFailure(existing.Result) ? record : existing);
    }

    internal static List<RuntimeFontMappingResultRecord> GetRuntimeMappingResults()
        => [.. RuntimeMappings.Values
            .OrderBy(x => x.Source, StringComparer.Ordinal)
            .ThenBy(x => x.Owner, StringComparer.Ordinal)
            .ThenBy(x => x.OriginalFont, StringComparer.Ordinal)];

    private static string NormalizeSource(string source)
    {
        if (source.StartsWith("MText", StringComparison.OrdinalIgnoreCase))
            return "MText";
        return string.IsNullOrWhiteSpace(source) ? "未知" : source;
    }

    private static bool IsFailure(string result)
        => result.StartsWith("映射失败", StringComparison.OrdinalIgnoreCase);

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
