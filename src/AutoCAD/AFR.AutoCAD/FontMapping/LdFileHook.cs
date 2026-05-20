using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// Bridges @-prefixed font-load requests without changing the AcGiTextStyle
/// font name that AutoCAD uses to decode vertical TrueType or MText big-font
/// content.
/// Redirects are executed only after a higher-level style or MText hook has
/// registered the original font name and replacement.
/// </summary>
internal static class LdFileHook
{
    private const string Tag = "LdFileHook";
    private const string LdFileExport = "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z";
    private const int FontTypeRegular = 0;
    private const int FontTypeShape = 2;
    private const int FontTypeBigFont = 4;

    private static NativeInlineHook<LdFileDelegate>? _hook;
    private static LdFileDelegate? _hookDelegate;
    private static readonly ConcurrentDictionary<string, IntPtr> NativeStringCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RegisteredRedirect> RegisteredRedirects =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> RedirectLogSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _hitCount;
    private static long _redirectCount;

    [ThreadStatic] private static bool _inHook;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LdFileDelegate(IntPtr fileName, int param2, IntPtr db, IntPtr desc);

    private sealed record RegisteredRedirect(
        string OriginalFont,
        string ReplacementFont,
        FontRedirectKind Kind,
        string Source,
        InlineFontType? InlineType);

    internal static bool IsInstalled => _hook?.IsInstalled == true;

    internal static void ClearRegisteredRedirects()
    {
        RegisteredRedirects.Clear();
        RedirectLogSeen.Clear();
    }

    internal static bool RegisterRedirect(
        string originalFont,
        string replacementFont,
        FontRedirectKind kind,
        string source,
        InlineFontType? inlineType = null)
    {
        string original = NormalizeLoadFontName(originalFont, kind);
        if (!IsRegisteredAtFontName(original, kind))
            return false;

        string replacement = NormalizeReplacementFontName(replacementFont, kind, original);
        if (string.IsNullOrWhiteSpace(replacement))
            return false;

        if (string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase))
            return false;

        string normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
        var request = new RegisteredRedirect(original, replacement, kind, normalizedSource, inlineType);
        string key = GetRedirectKey(original, kind);

        RegisteredRedirects.AddOrUpdate(
            key,
            static (_, incoming) => incoming,
            static (_, existing, incoming) => MergeRegisteredRedirect(existing, incoming),
            request);

        string logKey = $"register|{key}|{replacement}|{normalizedSource}|{inlineType}";
        if (RedirectLogSeen.TryAdd(logKey, 0))
        {
            DiagnosticLogger.Log(Tag,
                $"登记@字体加载映射: source={normalizedSource} kind={kind} " +
                $"'{original}' → '{replacement}' inline={inlineType?.ToString() ?? "none"}");
        }

        return true;
    }

    internal static void Install()
    {
        if (IsInstalled)
            return;

        IntPtr module = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过 ldfile 字体加载 Hook。");
            return;
        }

        IntPtr address = NativeInlineHookInterop.GetProcAddress(module, LdFileExport);
        if (address == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, "ldfile 导出未找到，跳过 @ 字体加载桥接。");
            return;
        }

        long delta = address.ToInt64() - module.ToInt64();
        if (delta <= 0 || delta > uint.MaxValue)
        {
            DiagnosticLogger.Log(Tag, "ldfile RVA 解析失败，跳过 @ 字体加载桥接。");
            return;
        }

        _hookDelegate = HookHandler;
        _hook = new NativeInlineHook<LdFileDelegate>(Tag, "ldfile", (uint)delta);
        _hook.InstallAtAddress(address, (uint)delta, _hookDelegate, 14, 64);
    }

    internal static void Uninstall()
    {
        DiagnosticLogger.Log(Tag,
            $"已卸载。HitCount={Interlocked.Read(ref _hitCount)}, Redirects={Interlocked.Read(ref _redirectCount)}");

        _hook?.Uninstall();
        _hook = null;
        _hookDelegate = null;
        ClearRegisteredRedirects();
        foreach (IntPtr ptr in NativeStringCache.Values)
        {
            try { Marshal.FreeHGlobal(ptr); } catch { }
        }
        NativeStringCache.Clear();
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _redirectCount, 0);
    }

    private static int HookHandler(IntPtr fileName, int param2, IntPtr db, IntPtr desc)
    {
        var trampoline = _hook?.TrampolineDelegate;
        if (trampoline == null)
            return -1;

        if (_inHook)
            return trampoline(fileName, param2, db, desc);

        _inHook = true;
        try
        {
            Interlocked.Increment(ref _hitCount);

            string original = ReadFontName(fileName);
            if (!TryGetRegisteredRedirect(original, param2, out RegisteredRedirect? request, out string normalized)
                || request == null)
            {
                return trampoline(fileName, param2, db, desc);
            }

            string replacement = request.ReplacementFont;
            if (string.Equals(normalized, replacement, StringComparison.OrdinalIgnoreCase))
                return trampoline(fileName, param2, db, desc);

            Interlocked.Increment(ref _redirectCount);
            if (request.InlineType.HasValue)
            {
                FontRuntimeMappingStore.RecordInlineMapping(
                    request.OriginalFont,
                    replacement,
                    request.InlineType.Value);
            }

            string logKey = $"redirect|{normalized}|{replacement}|{param2}|{request.Source}";
            if (RedirectLogSeen.TryAdd(logKey, 0))
            {
                DiagnosticLogger.Log(Tag,
                    $"执行@字体加载映射: source={request.Source} kind={request.Kind} " +
                    $"'{normalized}' → '{replacement}' param2={param2}");
            }

            IntPtr replacementPtr = NativeStringCache.GetOrAdd(
                replacement,
                static name => Marshal.StringToHGlobalUni(name));
            return trampoline(replacementPtr, param2, db, desc);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": ldfile Hook 异常", ex);
            return trampoline(fileName, param2, db, desc);
        }
        finally
        {
            _inHook = false;
        }
    }

    private static bool TryGetRegisteredRedirect(
        string fontName,
        int param2,
        out RegisteredRedirect? request,
        out string normalized)
    {
        request = null;
        normalized = string.Empty;

        string trueTypeName = NormalizeLoadTrueTypeName(fontName);
        if (param2 != FontTypeShape
            && IsRegisteredAtFontName(trueTypeName, FontRedirectKind.TrueType)
            && RegisteredRedirects.TryGetValue(GetRedirectKey(trueTypeName, FontRedirectKind.TrueType), out request))
        {
            normalized = trueTypeName;
            return true;
        }

        if (param2 == FontTypeShape)
            return false;

        string shxName = NormalizeLoadShxName(fontName);
        if (!IsRegisteredAtFontName(shxName, FontRedirectKind.ShxMain))
            return false;

        if (param2 == FontTypeBigFont)
        {
            if (RegisteredRedirects.TryGetValue(
                GetRedirectKey(shxName, FontRedirectKind.ShxBigFont),
                out request))
            {
                normalized = shxName;
                return true;
            }

            bool found = TryGetUniqueShxRegisteredRedirect(shxName, out request);
            if (found)
                normalized = shxName;
            return found;
        }

        if (param2 == FontTypeRegular)
        {
            if (RegisteredRedirects.TryGetValue(
                GetRedirectKey(shxName, FontRedirectKind.ShxMain),
                out request))
            {
                normalized = shxName;
                return true;
            }

            bool found = TryGetUniqueShxRegisteredRedirect(shxName, out request);
            if (found)
                normalized = shxName;
            return found;
        }

        bool fallback = TryGetUniqueShxRegisteredRedirect(shxName, out request);
        if (fallback)
            normalized = shxName;
        return fallback;
    }

    private static bool TryGetUniqueShxRegisteredRedirect(
        string normalized,
        out RegisteredRedirect? request)
    {
        request = null;
        bool foundMain = RegisteredRedirects.TryGetValue(
            GetRedirectKey(normalized, FontRedirectKind.ShxMain),
            out RegisteredRedirect? mainRequest);
        bool foundBig = RegisteredRedirects.TryGetValue(
            GetRedirectKey(normalized, FontRedirectKind.ShxBigFont),
            out RegisteredRedirect? bigRequest);

        if (foundMain == foundBig)
            return false;

        request = foundMain ? mainRequest : bigRequest;
        return request != null;
    }

    private static RegisteredRedirect MergeRegisteredRedirect(
        RegisteredRedirect existing,
        RegisteredRedirect incoming)
    {
        if (string.Equals(existing.ReplacementFont, incoming.ReplacementFont, StringComparison.OrdinalIgnoreCase)
            && existing.InlineType.HasValue)
        {
            return existing;
        }

        return incoming;
    }

    private static string GetRedirectKey(string normalized, FontRedirectKind kind)
        => string.Concat(normalized, "\u001F", kind);

    private static bool IsRegisteredAtFontName(string fontName, FontRedirectKind kind)
    {
        if (string.IsNullOrWhiteSpace(fontName) || fontName.Length <= 1 || fontName[0] != '@')
            return false;

        return kind == FontRedirectKind.TrueType
            ? !fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            : fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLoadFontName(string fontName, FontRedirectKind kind)
    {
        return kind == FontRedirectKind.TrueType
            ? NormalizeLoadTrueTypeName(fontName)
            : NormalizeLoadShxName(fontName);
    }

    private static string NormalizeReplacementFontName(
        string fontName,
        FontRedirectKind kind,
        string original)
    {
        if (kind != FontRedirectKind.TrueType)
            return FontRedirectResolver.EnsureShx(fontName);

        string replacement = NormalizeLoadTrueTypeName(fontName);
        if (string.IsNullOrWhiteSpace(replacement))
            return string.Empty;

        if (original.Length > 1 && original[0] == '@' && replacement[0] != '@')
            replacement = "@" + replacement.TrimStart('@');

        return replacement;
    }

    private static string NormalizeLoadTrueTypeName(string fontName)
    {
        return MTextFontParser.NormalizeTrueTypeFontName(fontName);
    }

    private static string NormalizeLoadShxName(string fontName)
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

    private static string ReadFontName(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return string.Empty;

        try
        {
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
