using System.IO;
using AFR.Services;

namespace AFR.FontMapping;

internal enum FontRedirectKind
{
    TrueType,
    ShxMain,
    ShxBigFont
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
        if (!FontAvailabilityIndex.IsShxAvailableWithAtFallback(shxName))
            return false;

        if (FontAvailabilityIndex.TryGetShxKind(shxName, out bool isBigFont))
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

    internal static bool IsAvailableTrueType(string name)
    {
        string normalized = NormalizeInputName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return FontAvailabilityIndex.IsTrueTypeAvailable(normalized);
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
