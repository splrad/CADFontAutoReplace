using System.IO;
using AFR.Services;

namespace AFR.FontMapping;

internal enum FontRedirectKind
{
    TrueType,
    ShxMain,
    ShxBigFont
}

internal readonly struct FontRedirectResolution
{
    internal FontRedirectResolution(
        string originalName,
        string lookupName,
        string redirectName,
        FontRedirectKind kind,
        bool hadAtPrefix,
        string reason)
    {
        OriginalName = originalName;
        LookupName = lookupName;
        RedirectName = redirectName;
        Kind = kind;
        HadAtPrefix = hadAtPrefix;
        Reason = reason;
    }

    internal string OriginalName { get; }
    internal string LookupName { get; }
    internal string RedirectName { get; }
    internal FontRedirectKind Kind { get; }
    internal bool HadAtPrefix { get; }
    internal string Reason { get; }
}

internal enum FontLogicalReplacementAction
{
    NoAction,
    DirectLogicalReplacement,
    RuntimeLoadBridge
}

internal readonly struct FontLogicalReplacement
{
    internal FontLogicalReplacement(
        FontLogicalReplacementAction action,
        string originalName,
        string lookupName,
        string replacementName,
        FontRedirectKind kind,
        bool preservesOriginalLoadRequest,
        string reason)
    {
        Action = action;
        OriginalName = originalName;
        LookupName = lookupName;
        ReplacementName = replacementName;
        Kind = kind;
        PreservesOriginalLoadRequest = preservesOriginalLoadRequest;
        Reason = reason;
    }

    internal FontLogicalReplacementAction Action { get; }
    internal string OriginalName { get; }
    internal string LookupName { get; }
    internal string ReplacementName { get; }
    internal FontRedirectKind Kind { get; }
    internal bool PreservesOriginalLoadRequest { get; }
    internal string Reason { get; }
}

internal static class FontRedirectResolver
{
    internal static string NormalizeInputName(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return string.Empty;

        string trimmed = fontName.Trim();
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
    }

    internal static bool HasAtPrefix(string fontName)
    {
        string normalized = NormalizeInputName(fontName);
        return normalized.Length > 1 && normalized[0] == '@';
    }

    internal static string StripLeadingAtPrefix(string fontName)
        => NormalizeInputName(fontName).TrimStart('@');

    internal static string GetRedirectSourceKey(string fontName, FontRedirectKind kind)
    {
        string lookupName = StripLeadingAtPrefix(fontName);
        return kind == FontRedirectKind.TrueType ? lookupName : EnsureShx(lookupName);
    }

    internal static FontLogicalReplacement ResolveLogicalFont(
        string fontName,
        FontRedirectKind kind,
        bool preserveOriginalLoadRequest = false)
    {
        string original = NormalizeInputName(fontName);
        if (string.IsNullOrWhiteSpace(original))
        {
            return new FontLogicalReplacement(
                FontLogicalReplacementAction.NoAction,
                string.Empty,
                string.Empty,
                string.Empty,
                kind,
                preserveOriginalLoadRequest,
                "空字体名");
        }

        bool hasAtPrefix = original[0] == '@';
        bool preserveLoadRequest = preserveOriginalLoadRequest || hasAtPrefix;
        string lookupName = hasAtPrefix ? original.TrimStart('@') : original;
        if (string.IsNullOrWhiteSpace(lookupName))
        {
            return new FontLogicalReplacement(
                FontLogicalReplacementAction.NoAction,
                original,
                string.Empty,
                string.Empty,
                kind,
                preserveLoadRequest,
                "空字体名");
        }

        if (preserveLoadRequest)
        {
            if (IsOriginalLoadFontAvailable(original, lookupName, kind, hasAtPrefix))
            {
                return new FontLogicalReplacement(
                    FontLogicalReplacementAction.NoAction,
                    original,
                    lookupName,
                    string.Empty,
                    kind,
                    preserveLoadRequest,
                    "原始加载字体可用");
            }

            if (TryResolveMissingFont(original, kind, out FontRedirectResolution runtimeResolution))
            {
                return new FontLogicalReplacement(
                    FontLogicalReplacementAction.RuntimeLoadBridge,
                    original,
                    runtimeResolution.LookupName,
                    runtimeResolution.RedirectName,
                    kind,
                    preserveLoadRequest,
                    runtimeResolution.Reason);
            }

            return new FontLogicalReplacement(
                FontLogicalReplacementAction.NoAction,
                original,
                lookupName,
                string.Empty,
                kind,
                preserveLoadRequest,
                "未找到可用运行时加载映射");
        }

        if (IsLogicalFontAvailable(original, kind))
        {
            return new FontLogicalReplacement(
                FontLogicalReplacementAction.NoAction,
                original,
                lookupName,
                string.Empty,
                kind,
                preserveLoadRequest,
                "字体可用");
        }

        if (!TryResolveConfiguredReplacement(kind, out string configured))
        {
            return new FontLogicalReplacement(
                FontLogicalReplacementAction.NoAction,
                original,
                lookupName,
                string.Empty,
                kind,
                preserveLoadRequest,
                "未找到可用配置字体");
        }

        string sourceKey = GetRedirectSourceKey(original, kind);
        if (string.Equals(sourceKey, configured, StringComparison.OrdinalIgnoreCase)
            || string.Equals(original, configured, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lookupName, configured, StringComparison.OrdinalIgnoreCase))
        {
            return new FontLogicalReplacement(
                FontLogicalReplacementAction.NoAction,
                original,
                lookupName,
                string.Empty,
                kind,
                preserveLoadRequest,
                "替换目标与原字体相同");
        }

        return new FontLogicalReplacement(
            FontLogicalReplacementAction.DirectLogicalReplacement,
            original,
            lookupName,
            configured,
            kind,
            preserveLoadRequest,
            "配置字体兜底");
    }

    internal static bool TryResolveMissingFont(
        string fontName,
        FontRedirectKind kind,
        out FontRedirectResolution resolution)
    {
        resolution = default;

        string original = NormalizeInputName(fontName);
        if (string.IsNullOrWhiteSpace(original))
            return false;

        bool hadAtPrefix = original[0] == '@';
        string lookupName = hadAtPrefix ? original.TrimStart('@') : original;
        if (string.IsNullOrWhiteSpace(lookupName))
            return false;

        if (hadAtPrefix
            && kind != FontRedirectKind.TrueType
            && TryResolveAvailableBase(lookupName, kind, out string baseFont))
        {
            resolution = new FontRedirectResolution(
                original,
                lookupName,
                baseFont,
                kind,
                hadAtPrefix,
                "基础字体可用");
            return true;
        }

        if (hadAtPrefix
            && kind == FontRedirectKind.TrueType
            && TryResolveAvailableBase(lookupName, kind, out string baseTrueType))
        {
            resolution = new FontRedirectResolution(
                original,
                lookupName,
                baseTrueType,
                kind,
                hadAtPrefix,
                "基础字体可用");
            return true;
        }

        if (TryResolveConfiguredReplacement(kind, out string configured))
        {
            resolution = new FontRedirectResolution(
                original,
                lookupName,
                configured,
                kind,
                hadAtPrefix,
                "配置字体兜底");
            return true;
        }

        return false;
    }

    internal static bool TryResolveAtPrefixedFont(
        string fontName,
        FontRedirectKind kind,
        out FontRedirectResolution resolution)
    {
        resolution = default;

        string original = NormalizeInputName(fontName);
        if (string.IsNullOrWhiteSpace(original) || original[0] != '@')
            return false;

        return TryResolveMissingFont(original, kind, out resolution);
    }

    internal static bool TryResolveConfiguredReplacement(
        FontRedirectKind kind,
        out string replacement)
    {
        replacement = string.Empty;

        string configured = kind switch
        {
            FontRedirectKind.TrueType => ConfigService.Instance.TrueTypeFont?.Trim() ?? string.Empty,
            FontRedirectKind.ShxBigFont => ConfigService.Instance.BigFont?.Trim() ?? string.Empty,
            _ => ConfigService.Instance.MainFont?.Trim() ?? string.Empty
        };

        configured = NormalizeInputName(configured).TrimStart('@');
        if (string.IsNullOrWhiteSpace(configured))
            return false;

        if (kind == FontRedirectKind.TrueType)
        {
            if (!IsAvailableTrueType(configured))
                return false;

            replacement = configured;
            return true;
        }

        string shxName = EnsureShx(configured);
        if (!HookShxFontIndex.IsAvailableWithAtFallback(shxName))
            return false;

        if (HookShxFontIndex.TryGetKind(shxName, out bool isBigFont))
        {
            if (kind == FontRedirectKind.ShxBigFont && !isBigFont)
                return false;
            if (kind == FontRedirectKind.ShxMain && isBigFont)
                return false;
        }
        else
        {
            return false;
        }

        replacement = shxName;
        return true;
    }

    internal static bool TryResolveAvailableBase(
        string lookupName,
        FontRedirectKind kind,
        out string baseFont)
    {
        baseFont = string.Empty;
        lookupName = NormalizeInputName(lookupName).TrimStart('@');
        if (string.IsNullOrWhiteSpace(lookupName))
            return false;

        if (kind == FontRedirectKind.TrueType)
        {
            if (!IsAvailableTrueType(lookupName))
                return false;

            baseFont = lookupName;
            return true;
        }

        string shxName = EnsureShx(lookupName);
        if (!HookShxFontIndex.IsExactAvailable(shxName)
            && !HookShxFontIndex.IsAvailableWithAtFallback(shxName))
        {
            return false;
        }

        if (HookShxFontIndex.TryGetKind(shxName, out bool isBigFont))
        {
            if (kind == FontRedirectKind.ShxBigFont && !isBigFont)
                return false;
            if (kind == FontRedirectKind.ShxMain && isBigFont)
                return false;
        }
        else
        {
            return false;
        }

        baseFont = shxName;
        return true;
    }

    internal static bool IsAvailableTrueType(string name)
    {
        string normalized = NormalizeInputName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return HookTrueTypeFontIndex.IsAvailable(normalized);
    }

    internal static bool IsTrueTypeFontFileName(string fontName)
        => FontDetector.IsTrueTypeFontFile(NormalizeInputName(fontName));

    internal static string EnsureShx(string name)
    {
        string normalized = NormalizeInputName(name).TrimStart('@');
        return normalized.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".shx";
    }

    private static bool IsOriginalLoadFontAvailable(
        string original,
        string lookupName,
        FontRedirectKind kind,
        bool hasAtPrefix)
    {
        if (kind == FontRedirectKind.TrueType)
            return IsAvailableTrueType(original);

        string shxName = NormalizeShxPreserveAt(original);
        if (!HookShxFontIndex.IsExactAvailable(shxName))
            return false;

        return HookShxFontIndex.TryGetKind(shxName, out bool isBigFont)
               && IsExpectedShxKind(kind, isBigFont);
    }

    private static bool IsLogicalFontAvailable(string fontName, FontRedirectKind kind)
    {
        if (kind == FontRedirectKind.TrueType)
            return IsAvailableTrueType(fontName);

        string shxName = NormalizeShxPreserveAt(fontName);
        if (!HookShxFontIndex.IsAvailableWithAtFallback(shxName))
            return false;

        return HookShxFontIndex.TryGetKind(shxName, out bool isBigFont)
               && IsExpectedShxKind(kind, isBigFont);
    }

    private static bool IsExpectedShxKind(FontRedirectKind kind, bool isBigFont)
    {
        return kind switch
        {
            FontRedirectKind.ShxBigFont => isBigFont,
            FontRedirectKind.ShxMain => !isBigFont,
            _ => false
        };
    }

    private static string NormalizeShxPreserveAt(string name)
    {
        string normalized = NormalizeInputName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".shx";
    }
}
