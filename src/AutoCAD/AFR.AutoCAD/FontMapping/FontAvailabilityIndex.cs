namespace AFR.FontMapping;

/// <summary>
/// Shared font availability API used by both stylesheet processing and runtime hooks.
/// </summary>
internal static class FontAvailabilityIndex
{
    internal static void InitializeAll()
    {
        InitializeShx();
        InitializeTrueType();
    }

    internal static void InitializeShx() => HookShxFontIndex.Initialize();

    internal static void InitializeTrueType() => HookTrueTypeFontIndex.Initialize();

    internal static bool IsShxAvailable(string fontName)
        => HookShxFontIndex.IsExactAvailable(fontName);

    internal static bool IsShxAvailableWithAtFallback(string fontName)
        => HookShxFontIndex.IsAvailableWithAtFallback(fontName);

    internal static bool TryGetShxKind(string fontName, out bool isBigFont)
        => HookShxFontIndex.TryGetKind(fontName, out isBigFont);

    internal static bool IsTrueTypeAvailable(string fontName)
        => HookTrueTypeFontIndex.IsAvailable(fontName);

    internal static bool IsTrueTypeBaseAvailable(string fontName)
        => HookTrueTypeFontIndex.IsAvailable(FontRedirectResolver.StripLeadingAtPrefix(fontName));

    internal static bool IsTrueTypeIndexReady => HookTrueTypeFontIndex.IsSystemIndexReady;

    internal static IReadOnlyCollection<string> GetAvailableTrueTypeFontNamesSnapshot()
        => HookTrueTypeFontIndex.GetAvailableFontNamesSnapshot();
}
