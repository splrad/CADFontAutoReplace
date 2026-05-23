using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 处理 AutoCAD ldfile 阶段的 SHX 运行时字体加载重定向。
/// <para>
/// 上层 Hook 先按 Style/MText 各自规则判断是否需要登记桥接；
/// TrueType 登记由 shpload 消费，ldfile 仅保留 SHX 主字体/大字体执行路径。
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
    private static readonly ConcurrentDictionary<string, byte> RedirectLogSeen =
        new(StringComparer.Ordinal);
    private static long _hitCount;
    private static long _redirectCount;

    [ThreadStatic] private static bool _inHook;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LdFileDelegate(IntPtr fileName, int param2, IntPtr db, IntPtr desc);

    internal sealed record RuntimeBridgeRedirect(
        string NormalizedRequest,
        string OriginalDisplayFont,
        string ReplacementFont,
        FontRedirectKind Kind,
        string Source,
        InlineFontType? InlineType);

    internal static bool IsInstalled => _hook?.IsInstalled == true;

    internal static (long HitCount, long RedirectCount) GetCountersSnapshot()
        => (Interlocked.Read(ref _hitCount), Interlocked.Read(ref _redirectCount));

    internal static void ClearRegisteredRedirects()
    {
        FontRuntimeRequestRegistry.Clear();
        RedirectLogSeen.Clear();
    }

    internal static bool TryGetRegisteredTrueTypeRedirect(
        string fontName,
        out RuntimeBridgeRedirect? redirect)
    {
        redirect = null;
        if (!FontRuntimeRequestRegistry.TryGetTrueTypeRequest(fontName, out FontRuntimeRequest? request, out string normalized)
            || request == null)
            return false;

        if (string.Equals(normalized, request.ReplacementFont, StringComparison.OrdinalIgnoreCase))
            return false;

        redirect = new RuntimeBridgeRedirect(
            normalized,
            request.OriginalDisplayFont,
            request.ReplacementFont,
            request.Kind,
            request.Source,
            request.InlineType);
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
        if (!FontRuntimeRequestRegistry.HasShxRequests)
            return trampoline(fileName, param2, db, desc);

        _inHook = true;
        try
        {
            string original = ReadFontName(fileName);
            // 未登记的字体请求必须原样放行，避免 ldfile 抢走上层 Hook 的决策权。
            if (!TryGetRegisteredRedirect(original, param2, out FontRuntimeRequest? request, out string normalized)
                || request == null)
            {
                return trampoline(fileName, param2, db, desc);
            }

            string replacement = request.ReplacementFont;
            if (string.Equals(normalized, replacement, StringComparison.OrdinalIgnoreCase))
                return trampoline(fileName, param2, db, desc);

            Interlocked.Increment(ref _redirectCount);
            FontRuntimeRequestRegistry.MarkHit(request.NormalizedRequest, request.Kind);
            FontRuntimeMappingStore.RecordRuntimeMapping(request, "LdFileHook", "已映射");

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
        out FontRuntimeRequest? request,
        out string normalized)
    {
        request = null;
        normalized = string.Empty;

        // TrueType 已交给 shpload 处理；ldfile 仅保留 SHX 主字体/大字体桥接。
        if (param2 == FontTypeShape)
            return false;

        string shxName = NormalizeLoadShxName(fontName);
        if (!IsRegisterableLoadFontName(shxName, FontRedirectKind.ShxMain))
            return false;

        if (param2 == FontTypeBigFont)
        {
            if (FontRuntimeRequestRegistry.TryGetShxRequest(
                shxName,
                FontRedirectKind.ShxBigFont,
                out request,
                out _))
            {
                normalized = shxName;
                return true;
            }

            bool found = FontRuntimeRequestRegistry.TryGetUniqueShxRequest(shxName, out request, out _);
            if (found)
                normalized = shxName;
            return found;
        }

        if (param2 == FontTypeRegular)
        {
            if (FontRuntimeRequestRegistry.TryGetShxRequest(
                shxName,
                FontRedirectKind.ShxMain,
                out request,
                out _))
            {
                normalized = shxName;
                return true;
            }

            bool found = FontRuntimeRequestRegistry.TryGetUniqueShxRequest(shxName, out request, out _);
            if (found)
                normalized = shxName;
            return found;
        }

        bool fallback = FontRuntimeRequestRegistry.TryGetUniqueShxRequest(shxName, out request, out _);
        if (fallback)
            normalized = shxName;
        return fallback;
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

    private static bool IsRegisterableLoadFontName(string fontName, FontRedirectKind kind)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        return kind == FontRedirectKind.TrueType
            ? !fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            : fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
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
