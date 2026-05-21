using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 只处理 AutoCAD ldfile 阶段的运行时字体加载重定向。
/// <para>
/// 上层 Hook 先按 Style/MText 各自规则判断是否需要交给 LdFileHook；
/// 这里不负责样式表永久替换，也不决定哪些字体应该映射。
/// </para>
/// <para>
/// 登记后仅在 ldfile 实际请求同一个原始加载字体时替换加载文件名，
/// 保留 AcGiTextStyle/MText 原始字体名，避免破坏竖排 TrueType 和大字体解码上下文。
/// </para>
/// </summary>
internal static class LdFileHook
{
    private const string Tag = "LdFileHook";

    // ldfile 的 param2 用于区分普通字体、ShapeFile 和大字体；ShapeFile 不参与字体兜底。
    private const int FontTypeRegular = 0;
    private const int FontTypeShape = 2;
    private const int FontTypeBigFont = 4;

    private static NativeInlineHook<LdFileDelegate>? _hook;
    private static LdFileDelegate? _hookDelegate;
    private static readonly ConcurrentDictionary<string, IntPtr> NativeStringCache =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RegisteredRedirect> RegisteredRedirects =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RegisteredRedirect?> FoldedShxRegisteredRedirects =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> RedirectLogSeen =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> FoldedRedirectAmbiguityLogSeen =
        new(StringComparer.Ordinal);
    private static long _hitCount;
    private static long _redirectCount;

    [ThreadStatic] private static bool _inHook;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LdFileDelegate(IntPtr fileName, int param2, IntPtr db, IntPtr desc);

    private sealed record RegisteredRedirect(
        string OriginalFont,
        string OriginalDisplayFont,
        string ReplacementFont,
        FontRedirectKind Kind,
        string Source,
        InlineFontType? InlineType);

    internal static bool IsInstalled => _hook?.IsInstalled == true;

    internal static void ClearRegisteredRedirects()
    {
        RegisteredRedirects.Clear();
        FoldedShxRegisteredRedirects.Clear();
        RedirectLogSeen.Clear();
        FoldedRedirectAmbiguityLogSeen.Clear();
    }

    internal static bool TryRegisterResolvedAtFont(
        string originalFont,
        FontRedirectKind kind,
        string source,
        InlineFontType? inlineType,
        string? originalDisplayName,
        out string sourceKey,
        out string replacement)
    {
        sourceKey = string.Empty;
        replacement = string.Empty;

        // 是否交给 LdFileHook 由调用方决定；这里只接受共享决策后的运行时加载桥接。
        string original = NormalizeLoadFontName(originalFont, kind);
        if (!IsRegisterableLoadFontName(original, kind))
            return false;
        string displayOriginal = NormalizeLoadFontName(originalDisplayName ?? originalFont, kind);
        if (string.IsNullOrWhiteSpace(displayOriginal))
            displayOriginal = original;

        FontLogicalReplacement resolution = FontRedirectResolver.ResolveLogicalFont(
            original,
            kind,
            preserveOriginalLoadRequest: true);
        if (resolution.Action != FontLogicalReplacementAction.RuntimeLoadBridge)
            return false;

        string resolved = NormalizeReplacementFontName(resolution.ReplacementName, kind, original);
        if (string.IsNullOrWhiteSpace(resolved)
            || string.Equals(original, resolved, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
        var request = new RegisteredRedirect(original, displayOriginal, resolved, kind, normalizedSource, inlineType);
        string key = GetRedirectKey(original, kind);

        RegisteredRedirect registeredRequest = RegisteredRedirects.AddOrUpdate(
            key,
            request,
            (_, existing) => MergeRegisteredRedirect(existing, request));
        AddFoldedShxRegisteredRedirect(original, kind, registeredRequest);

        string logKey = $"register|{key}|{resolved}|{normalizedSource}|{inlineType}";
        if (RedirectLogSeen.TryAdd(logKey, 0))
        {
            DiagnosticLogger.Log(Tag,
                $"登记字体加载桥接: source={normalizedSource} kind={kind} " +
                $"'{displayOriginal}' → '{resolved}' inline={inlineType?.ToString() ?? "none"}");
        }

        sourceKey = original;
        replacement = resolved;
        return true;
    }

    internal static void Install()
    {
        if (IsInstalled)
            return;

        if (PlatformManager.Platform is not INativeFontHookExportsProvider exports)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.DisplayName} 未提供 ldfile Hook 导出定义，跳过字体加载桥接。");
            return;
        }

        IntPtr module = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过 ldfile 字体加载 Hook。");
            return;
        }

        NativeHookTarget target = exports.NativeFontHookProfile.LdFile;
        if (!TryGetExportAddress(module, target, out IntPtr address, out uint resolvedRva))
        {
            DiagnosticLogger.Log(Tag, "ldfile 入口未通过强校验，跳过字体加载桥接。");
            return;
        }

        _hookDelegate = HookHandler;
        _hook = new NativeInlineHook<LdFileDelegate>(Tag, target.Name, target.Rva ?? resolvedRva);
        _hook.InstallAtAddress(
            address,
            resolvedRva,
            _hookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
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

        Interlocked.Increment(ref _hitCount);
        if (RegisteredRedirects.IsEmpty)
            return trampoline(fileName, param2, db, desc);

        _inHook = true;
        try
        {
            string original = ReadFontName(fileName);
            // 未登记的字体请求必须原样放行，避免 ldfile 抢走上层 Hook 的决策权。
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
                    request.OriginalDisplayFont,
                    replacement,
                    request.InlineType.Value);
            }

            string logKey = $"redirect|{normalized}|{replacement}|{param2}|{request.Source}";
            if (RedirectLogSeen.TryAdd(logKey, 0))
            {
                DiagnosticLogger.Log(Tag,
                    $"执行字体加载桥接: source={request.Source} kind={request.Kind} " +
                    $"'{request.OriginalDisplayFont}' → '{replacement}' request='{normalized}' param2={param2}");
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

        // TrueType 和 SHX 使用不同归一化规则，必须按注册类型和 ldfile 参数分别匹配。
        string trueTypeName = NormalizeLoadTrueTypeName(fontName);
        if (param2 != FontTypeShape
            && IsRegisterableLoadFontName(trueTypeName, FontRedirectKind.TrueType)
            && RegisteredRedirects.TryGetValue(GetRedirectKey(trueTypeName, FontRedirectKind.TrueType), out request))
        {
            normalized = trueTypeName;
            return true;
        }

        if (param2 == FontTypeShape)
            return false;

        string shxName = NormalizeLoadShxName(fontName);
        if (!IsRegisterableLoadFontName(shxName, FontRedirectKind.ShxMain))
            return false;

        if (param2 == FontTypeBigFont)
        {
            if (TryGetShxRegisteredRedirect(
                shxName,
                FontRedirectKind.ShxBigFont,
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
            if (TryGetShxRegisteredRedirect(
                shxName,
                FontRedirectKind.ShxMain,
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
        // 部分 DWG 的 ldfile 参数不稳定；只有主/大字体注册唯一时才允许按 SHX 名称兜底。
        request = null;
        bool foundMain = TryGetShxRegisteredRedirect(
            normalized,
            FontRedirectKind.ShxMain,
            out RegisteredRedirect? mainRequest);
        bool foundBig = TryGetShxRegisteredRedirect(
            normalized,
            FontRedirectKind.ShxBigFont,
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

    private static bool TryGetShxRegisteredRedirect(
        string normalized,
        FontRedirectKind kind,
        out RegisteredRedirect? request)
    {
        if (RegisteredRedirects.TryGetValue(GetRedirectKey(normalized, kind), out request))
            return true;

        return TryGetFoldedShxRegisteredRedirect(normalized, kind, out request);
    }

    private static bool TryGetFoldedShxRegisteredRedirect(
        string normalized,
        FontRedirectKind kind,
        out RegisteredRedirect? request)
    {
        request = null;
        string foldedKey = GetFoldedShxRedirectKey(normalized, kind);
        if (!FoldedShxRegisteredRedirects.TryGetValue(foldedKey, out request))
            return false;

        if (request != null)
            return true;

        LogFoldedRedirectAmbiguity(normalized, kind);
        return false;
    }

    private static void AddFoldedShxRegisteredRedirect(
        string normalized,
        FontRedirectKind kind,
        RegisteredRedirect request)
    {
        if (kind == FontRedirectKind.TrueType)
            return;

        string foldedKey = GetFoldedShxRedirectKey(normalized, kind);
        FoldedShxRegisteredRedirects.AddOrUpdate(
            foldedKey,
            request,
            (_, existing) => existing != null
                             && string.Equals(existing.OriginalFont, request.OriginalFont, StringComparison.OrdinalIgnoreCase)
                ? MergeRegisteredRedirect(existing, request)
                : null);
    }

    private static string GetFoldedShxRedirectKey(string normalized, FontRedirectKind kind)
        => string.Concat(kind, "\u001F", normalized.ToUpperInvariant());

    private static void LogFoldedRedirectAmbiguity(string normalized, FontRedirectKind kind)
    {
        string logKey = string.Concat("folded-redirect-ambiguous|", kind, "|", normalized.ToUpperInvariant());
        if (FoldedRedirectAmbiguityLogSeen.TryAdd(logKey, 0))
        {
            DiagnosticLogger.Log(Tag,
                $"ldfile SHX 请求大小写恢复存在歧义，已跳过: kind={kind} request='{normalized}'");
        }
    }

    private static bool TryGetExportAddress(
        IntPtr module,
        NativeHookTarget target,
        out IntPtr address,
        out uint rva)
    {
        address = IntPtr.Zero;
        rva = 0;

        if (!target.IsEnabled || string.IsNullOrWhiteSpace(target.ExportName))
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：{target.DisabledReason ?? "缺少导出符号"}");
            return false;
        }

        string exportName = target.ExportName!;
        address = NativeInlineHookInterop.GetProcAddress(module, exportName);
        if (address == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 导出未找到，跳过。");
            return false;
        }

        long delta = address.ToInt64() - module.ToInt64();
        if (delta <= 0 || delta > uint.MaxValue)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} RVA 解析失败，跳过。Address=0x{address.ToInt64():X}");
            address = IntPtr.Zero;
            return false;
        }

        rva = (uint)delta;
        if (target.Rva.HasValue && target.Rva.Value != rva)
        {
            DiagnosticLogger.Log(Tag,
                $"{target.Name} RVA 不匹配，跳过。Expected=0x{target.Rva.Value:X}, Actual=0x{rva:X}");
            address = IntPtr.Zero;
            rva = 0;
            return false;
        }

        DiagnosticLogger.Log(Tag, $"{target.Name} 导出解析成功。RVA=0x{rva:X}");
        return true;
    }

    private static string GetRedirectKey(string normalized, FontRedirectKind kind)
        => string.Concat(normalized, "\u001F", kind);

    private static bool IsRegisterableLoadFontName(string fontName, FontRedirectKind kind)
    {
        if (string.IsNullOrWhiteSpace(fontName))
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
        // TrueType 替换保留 @ 前缀以保留竖排语义；SHX 替换交给 ldfile 加载基础 shx 文件。
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
