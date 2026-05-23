using System.Collections.Concurrent;
using System.IO;
using AFR.Services;

namespace AFR.FontMapping;

internal sealed record FontRuntimeRequest(
    string NormalizedRequest,
    string OriginalDisplayFont,
    string BaseFont,
    string ReplacementFont,
    FontRedirectKind Kind,
    string Source,
    string Owner,
    InlineFontType? InlineType,
    string ExecutingHook);

internal sealed record FontRuntimeRequestDiagnostic(
    string NormalizedRequest,
    string OriginalDisplayFont,
    string BaseFont,
    string ReplacementFont,
    FontRedirectKind Kind,
    string Source,
    string Owner,
    InlineFontType? InlineType,
    string ExecutingHook,
    bool Hit);

internal static class FontRuntimeRequestRegistry
{
    private const string Tag = "FontRuntimeRequestRegistry";

    private static readonly ConcurrentDictionary<string, FontRuntimeRequest> Requests =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, FontRuntimeRequest?> FoldedShxRequests =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> TrueTypeRequestKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> ShxRequestKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> HitKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> LogSeen =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> FoldedAmbiguityLogSeen =
        new(StringComparer.Ordinal);

    internal static bool HasTrueTypeRequests => !TrueTypeRequestKeys.IsEmpty;

    internal static bool HasShxRequests => !ShxRequestKeys.IsEmpty;

    internal static void Clear()
    {
        Requests.Clear();
        FoldedShxRequests.Clear();
        TrueTypeRequestKeys.Clear();
        ShxRequestKeys.Clear();
        HitKeys.Clear();
        LogSeen.Clear();
        FoldedAmbiguityLogSeen.Clear();
    }

    internal static IReadOnlyList<FontRuntimeRequestDiagnostic> GetDiagnosticsSnapshot()
    {
        return Requests
            .Select(pair =>
            {
                FontRuntimeRequest request = pair.Value;
                bool hit = HitKeys.ContainsKey(GetRequestKey(request.NormalizedRequest, request.Kind));
                return new FontRuntimeRequestDiagnostic(
                    request.NormalizedRequest,
                    request.OriginalDisplayFont,
                    request.BaseFont,
                    request.ReplacementFont,
                    request.Kind,
                    request.Source,
                    request.Owner,
                    request.InlineType,
                    request.ExecutingHook,
                    hit);
            })
            .OrderBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Kind.ToString(), StringComparer.Ordinal)
            .ThenBy(item => item.OriginalDisplayFont, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool TryRegisterResolvedRequest(
        string originalFont,
        FontRedirectKind kind,
        string source,
        string owner,
        InlineFontType? inlineType,
        string? originalDisplayName,
        out string sourceKey,
        out string replacement)
    {
        sourceKey = string.Empty;
        replacement = string.Empty;

        string original = NormalizeRequestName(originalFont, kind);
        if (!IsRegisterableRequestName(original, kind))
            return false;

        FontLogicalReplacement resolution = FontRedirectResolver.ResolveLogicalFont(
            original,
            kind,
            preserveOriginalLoadRequest: true);
        if (resolution.Action != FontLogicalReplacementAction.RuntimeLoadBridge)
            return false;

        string resolved = NormalizeReplacementName(resolution.ReplacementName, kind);
        if (string.IsNullOrWhiteSpace(resolved)
            || string.Equals(original, resolved, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string displayOriginal = NormalizeRequestName(originalDisplayName ?? originalFont, kind);
        if (string.IsNullOrWhiteSpace(displayOriginal))
            displayOriginal = original;

        string normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
        string normalizedOwner = string.IsNullOrWhiteSpace(owner) ? string.Empty : owner.Trim();
        string executingHook = kind == FontRedirectKind.TrueType ? "ShpLoadHook" : "LdFileHook";
        string baseFont = FontRedirectResolver.HasAtPrefix(original)
            ? NormalizeBaseFont(FontRedirectResolver.StripLeadingAtPrefix(original), kind)
            : string.Empty;

        var request = new FontRuntimeRequest(
            original,
            displayOriginal,
            baseFont,
            resolved,
            kind,
            normalizedSource,
            normalizedOwner,
            inlineType,
            executingHook);
        string key = GetRequestKey(original, kind);

        FontRuntimeRequest registered = Requests.AddOrUpdate(
            key,
            request,
            (_, existing) => Merge(existing, request));
        if (kind == FontRedirectKind.TrueType)
            TrueTypeRequestKeys.TryAdd(key, 0);
        else
            ShxRequestKeys.TryAdd(key, 0);
        AddFoldedShxRequest(original, kind, registered);

        string logKey = $"register|{key}|{resolved}|{normalizedSource}|{normalizedOwner}|{inlineType}";
        if (LogSeen.TryAdd(logKey, 0))
        {
            DiagnosticLogger.Log(Tag,
                $"登记文件级字体请求: source={normalizedSource} owner='{normalizedOwner}' kind={kind} " +
                $"original='{displayOriginal}' base='{baseFont}' target='{resolved}' hook={executingHook} inline={inlineType?.ToString() ?? "none"}");
        }

        sourceKey = original;
        replacement = resolved;
        return true;
    }

    internal static bool TryGetTrueTypeRequest(string fontName, out FontRuntimeRequest? request, out string normalized)
    {
        request = null;
        normalized = NormalizeRequestName(fontName, FontRedirectKind.TrueType);
        if (!IsRegisterableRequestName(normalized, FontRedirectKind.TrueType))
            return false;

        return Requests.TryGetValue(GetRequestKey(normalized, FontRedirectKind.TrueType), out request);
    }

    internal static bool TryGetShxRequest(
        string fontName,
        FontRedirectKind kind,
        out FontRuntimeRequest? request,
        out string normalized)
    {
        request = null;
        normalized = NormalizeRequestName(fontName, kind);
        if (!IsRegisterableRequestName(normalized, kind))
            return false;

        if (Requests.TryGetValue(GetRequestKey(normalized, kind), out request))
            return true;

        string foldedKey = GetFoldedShxKey(normalized, kind);
        if (!FoldedShxRequests.TryGetValue(foldedKey, out request))
            return false;

        if (request != null)
            return true;

        LogFoldedAmbiguity(normalized, kind);
        return false;
    }

    internal static bool TryGetUniqueShxRequest(string fontName, out FontRuntimeRequest? request, out string normalized)
    {
        request = null;
        normalized = NormalizeRequestName(fontName, FontRedirectKind.ShxMain);
        if (!IsRegisterableRequestName(normalized, FontRedirectKind.ShxMain))
            return false;

        bool foundMain = TryGetShxRequest(normalized, FontRedirectKind.ShxMain, out FontRuntimeRequest? mainRequest, out _);
        bool foundBig = TryGetShxRequest(normalized, FontRedirectKind.ShxBigFont, out FontRuntimeRequest? bigRequest, out _);
        if (foundMain == foundBig)
            return false;

        request = foundMain ? mainRequest : bigRequest;
        return request != null;
    }

    internal static void MarkHit(string normalizedFont, FontRedirectKind kind)
    {
        string normalized = NormalizeRequestName(normalizedFont, kind);
        if (!string.IsNullOrWhiteSpace(normalized))
            HitKeys.TryAdd(GetRequestKey(normalized, kind), 0);
    }

    private static FontRuntimeRequest Merge(FontRuntimeRequest existing, FontRuntimeRequest incoming)
    {
        return incoming;
    }

    private static void AddFoldedShxRequest(string normalized, FontRedirectKind kind, FontRuntimeRequest request)
    {
        if (kind == FontRedirectKind.TrueType)
            return;

        string foldedKey = GetFoldedShxKey(normalized, kind);
        FoldedShxRequests.AddOrUpdate(
            foldedKey,
            request,
            (_, existing) => existing != null
                             && string.Equals(existing.NormalizedRequest, request.NormalizedRequest, StringComparison.OrdinalIgnoreCase)
                ? Merge(existing, request)
                : null);
    }

    private static void LogFoldedAmbiguity(string normalized, FontRedirectKind kind)
    {
        string logKey = string.Concat("folded-ambiguous|", kind, "|", normalized.ToUpperInvariant());
        if (FoldedAmbiguityLogSeen.TryAdd(logKey, 0))
        {
            DiagnosticLogger.Log(Tag,
                $"SHX 请求大小写恢复存在歧义，已跳过: kind={kind} request='{normalized}'");
        }
    }

    private static string NormalizeRequestName(string fontName, FontRedirectKind kind)
        => kind == FontRedirectKind.TrueType
            ? MTextFontParser.NormalizeTrueTypeFontName(fontName)
            : NormalizeShxName(fontName);

    private static string NormalizeReplacementName(string fontName, FontRedirectKind kind)
        => kind == FontRedirectKind.TrueType
            ? MTextFontParser.NormalizeTrueTypeFontName(fontName).TrimStart('@')
            : FontRedirectResolver.EnsureShx(fontName);

    private static string NormalizeBaseFont(string fontName, FontRedirectKind kind)
        => kind == FontRedirectKind.TrueType
            ? MTextFontParser.NormalizeTrueTypeFontName(fontName)
            : FontRedirectResolver.EnsureShx(fontName);

    private static string NormalizeShxName(string fontName)
    {
        string normalized = FontRedirectResolver.NormalizeInputName(fontName);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        string extension = Path.GetExtension(normalized);
        if (!string.IsNullOrEmpty(extension)
            && !normalized.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return normalized.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".shx";
    }

    private static bool IsRegisterableRequestName(string fontName, FontRedirectKind kind)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        return kind == FontRedirectKind.TrueType
            ? !fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            : fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRequestKey(string normalized, FontRedirectKind kind)
        => string.Concat(normalized, "\u001F", kind);

    private static string GetFoldedShxKey(string normalized, FontRedirectKind kind)
        => string.Concat(kind, "\u001F", normalized.ToUpperInvariant());
}
