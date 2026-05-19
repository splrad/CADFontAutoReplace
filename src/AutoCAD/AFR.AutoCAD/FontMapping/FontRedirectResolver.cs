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

        if (hadAtPrefix && TryResolveAvailableBase(lookupName, kind, out string baseFont))
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
        if (!FontAvailabilityIndex.IsKnownAvailableFont(shxName))
            return false;

        if (FontAvailabilityIndex.TryGetKnownShxFontKind(shxName, out bool isBigFont))
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
        if (!FontAvailabilityIndex.IsExactKnownAvailableFont(shxName)
            && !FontAvailabilityIndex.IsKnownAvailableFont(shxName))
        {
            return false;
        }

        if (FontAvailabilityIndex.TryGetKnownShxFontKind(shxName, out bool isBigFont))
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
        string normalized = NormalizeInputName(name).TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (FontDetector.IsSystemFont(normalized))
            return true;

        if (FontAvailabilityIndex.IsKnownAvailableFont(normalized))
            return true;

        if (FontDetector.IsTrueTypeFontFile(normalized))
        {
            string familyCandidate = Path.GetFileNameWithoutExtension(normalized);
            return !string.IsNullOrWhiteSpace(familyCandidate)
                   && FontDetector.IsSystemFont(familyCandidate);
        }

        return false;
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
}
