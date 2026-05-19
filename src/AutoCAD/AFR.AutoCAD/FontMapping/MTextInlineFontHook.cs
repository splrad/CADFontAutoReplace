using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// Hooks AcGiTextStyle font setters so missing inline MText fonts can be
/// mapped without rewriting MText contents.
/// </summary>
internal static class MTextInlineFontHook
{
    private const string Tag = "MTextInlineFontHook";

    private static NativeInlineHook<AcGiTextStyleSetFontDelegate>? _setFontHook;
    private static NativeInlineHook<AcGiTextStyleFileNameCtorDelegate>? _fileNameCtorHook;
    private static NativeInlineHook<AcGiTextStyleSetFileNameDelegate>? _setFileNameHook;
    private static NativeInlineHook<AcGiTextStyleSetFileNameDelegate>? _setBigFontFileNameHook;
    private static NativeInlineHook<AcDbMTextExplodeFragmentsDelegate>? _explodeFragmentsHook;
    private static AcGiTextStyleSetFontDelegate? _setFontHookDelegate;
    private static AcGiTextStyleFileNameCtorDelegate? _fileNameCtorHookDelegate;
    private static AcGiTextStyleSetFileNameDelegate? _setFileNameHookDelegate;
    private static AcGiTextStyleSetFileNameDelegate? _setBigFontFileNameHookDelegate;
    private static AcDbMTextExplodeFragmentsDelegate? _explodeFragmentsHookDelegate;
    private static readonly ConcurrentDictionary<string, IntPtr> NativeTypefaceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, IntPtr> NativeFileNameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> RedirectLogSeen = new(StringComparer.OrdinalIgnoreCase);

    [ThreadStatic] private static bool _inSetFontHook;
    [ThreadStatic] private static bool _inFileNameCtorHook;
    [ThreadStatic] private static bool _inSetFileNameHook;
    [ThreadStatic] private static bool _inSetBigFontFileNameHook;
    [ThreadStatic] private static bool _inInlineFontRedirect;
    [ThreadStatic] private static bool _inExplodeFragmentsHook;
    [ThreadStatic] private static int _mTextScopeDepth;

    internal static bool IsInsideInlineFontHook
    {
        get
        {
            return _inInlineFontRedirect
                   || _mTextScopeDepth > 0
                   ;
        }
    }

    private static bool IsInsideMTextScope => _mTextScopeDepth > 0;

    internal static bool IsInstalled =>
        _setFontHook?.IsInstalled == true
        || _fileNameCtorHook?.IsInstalled == true
        || _setFileNameHook?.IsInstalled == true
        || _setBigFontFileNameHook?.IsInstalled == true
        || _explodeFragmentsHook?.IsInstalled == true
        ;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AcGiTextStyleSetFontDelegate(
        IntPtr self,
        IntPtr typeface,
        byte bold,
        byte italic,
        int charset,
        int pitch,
        int family);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AcGiTextStyleFileNameCtorDelegate(
        IntPtr self,
        IntPtr fontName,
        IntPtr bigFontName,
        double textSize,
        double xScale,
        double obliqueAngle,
        double trackingPercent,
        byte isBackward,
        byte isUpsideDown,
        byte isVertical,
        byte isOverlined,
        byte isUnderlined,
        byte isStrikethrough,
        IntPtr styleName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AcGiTextStyleSetFileNameDelegate(IntPtr self, IntPtr fontName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AcDbMTextExplodeFragmentsDelegate(
        IntPtr self,
        IntPtr callback,
        IntPtr callbackParam,
        IntPtr worldDraw);

    internal static void Install()
    {
        if (PlatformManager.Platform is not INativeFontHookExportsProvider exports)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.DisplayName} 未提供字体 Hook 导出定义，跳过 MText 内联字体 Hook。");
            return;
        }

        IntPtr module = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过 MText 内联字体 Hook。");
            return;
        }

        TryInstallExplodeFragmentsScope(module, exports.AcDbMTextExplodeFragmentsExport);
        if (_explodeFragmentsHook?.IsInstalled != true)
        {
            DiagnosticLogger.Log(Tag, "MText 作用域 Hook 未安装，MText 内联字体映射 fail closed。");
            return;
        }

        TryInstallSetFontHook(module, exports.AcGiTextStyleSetFontExport);
        TryInstallFileNameCtorHook(module, exports.AcGiTextStyleFileNameCtorExport);
        TryInstallSetFileNameHook(module, exports.AcGiTextStyleSetFileNameExport);
        TryInstallSetBigFontFileNameHook(module, exports.AcGiTextStyleSetBigFontFileNameExport);
    }

    internal static void Uninstall()
    {
        _explodeFragmentsHook?.Uninstall();
        _explodeFragmentsHook = null;
        _explodeFragmentsHookDelegate = null;

        _setFontHook?.Uninstall();
        _setFontHook = null;
        _setFontHookDelegate = null;
        _fileNameCtorHook?.Uninstall();
        _fileNameCtorHook = null;
        _fileNameCtorHookDelegate = null;
        _setFileNameHook?.Uninstall();
        _setFileNameHook = null;
        _setFileNameHookDelegate = null;
        _setBigFontFileNameHook?.Uninstall();
        _setBigFontFileNameHook = null;
        _setBigFontFileNameHookDelegate = null;
        RedirectLogSeen.Clear();
    }

    private static void TryInstallSetFontHook(IntPtr module, string exportName)
    {
        if (_setFontHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, exportName, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::setFont 导出未找到，跳过 MText 内联 TrueType Hook。");
            return;
        }

        _setFontHookDelegate = SetFontHookHandler;
        _setFontHook = new NativeInlineHook<AcGiTextStyleSetFontDelegate>(
            Tag,
            "AcGiTextStyle::setFont",
            rva);

        _setFontHook.InstallAtAddress(address, rva, _setFontHookDelegate, 14, 64);
    }

    private static void TryInstallFileNameCtorHook(IntPtr module, string exportName)
    {
        if (_fileNameCtorHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, exportName, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::AcGiTextStyle(font,bigFont) 导出未找到，跳过构造函数 Hook。");
            return;
        }

        _fileNameCtorHookDelegate = FileNameCtorHookHandler;
        _fileNameCtorHook = new NativeInlineHook<AcGiTextStyleFileNameCtorDelegate>(
            Tag,
            "AcGiTextStyle::AcGiTextStyle(font,bigFont)",
            rva);

        _fileNameCtorHook.InstallAtAddress(address, rva, _fileNameCtorHookDelegate, 14, 96);
    }

    private static void TryInstallSetFileNameHook(IntPtr module, string exportName)
    {
        if (_setFileNameHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, exportName, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::setFileName 导出未找到，跳过 MText 内联 SHX 主字体 Hook。");
            return;
        }

        _setFileNameHookDelegate = SetFileNameHookHandler;
        _setFileNameHook = new NativeInlineHook<AcGiTextStyleSetFileNameDelegate>(
            Tag,
            "AcGiTextStyle::setFileName",
            rva);

        _setFileNameHook.InstallAtAddress(address, rva, _setFileNameHookDelegate, 14, 64);
    }

    private static void TryInstallSetBigFontFileNameHook(IntPtr module, string exportName)
    {
        if (_setBigFontFileNameHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, exportName, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::setBigFontFileName 导出未找到，跳过 MText 内联 SHX 大字体 Hook。");
            return;
        }

        _setBigFontFileNameHookDelegate = SetBigFontFileNameHookHandler;
        _setBigFontFileNameHook = new NativeInlineHook<AcGiTextStyleSetFileNameDelegate>(
            Tag,
            "AcGiTextStyle::setBigFontFileName",
            rva);

        _setBigFontFileNameHook.InstallAtAddress(address, rva, _setBigFontFileNameHookDelegate, 14, 64);
    }

    private static void TryInstallExplodeFragmentsScope(IntPtr module, string exportName)
    {
        if (_explodeFragmentsHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, exportName, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcDbMText::explodeFragments 导出未找到，跳过 MText 作用域 Hook。");
            return;
        }

        _explodeFragmentsHookDelegate = ExplodeFragmentsHookHandler;
        _explodeFragmentsHook = new NativeInlineHook<AcDbMTextExplodeFragmentsDelegate>(
            Tag,
            "AcDbMText::explodeFragments scope",
            rva);

        _explodeFragmentsHook.InstallAtAddress(address, rva, _explodeFragmentsHookDelegate, 14, 64);
    }

    private static int SetFontHookHandler(
        IntPtr self,
        IntPtr typeface,
        byte bold,
        byte italic,
        int charset,
        int pitch,
        int family)
    {
        var trampoline = _setFontHook?.TrampolineDelegate;
        if (trampoline == null)
            return 0;

        if (_inSetFontHook || !IsInsideMTextScope || StyleTextStyleHook.IsInsideStyleRuntimeOperation)
            return trampoline(self, typeface, bold, italic, charset, pitch, family);

        _inSetFontHook = true;
        try
        {
            string fontName = Marshal.PtrToStringUni(typeface) ?? string.Empty;
            string? replacement = ResolveMissingTrueTypeReplacement(fontName);
            if (replacement != null)
            {
                IntPtr replacementPtr = NativeTypefaceCache.GetOrAdd(
                    replacement,
                    static name => Marshal.StringToHGlobalUni(name));

                FontRuntimeMappingStore.RecordInlineMapping(fontName, replacement, InlineFontType.TrueType);

                string redirectKey = $"{fontName}|{replacement}|{bold}|{italic}|{charset}|{pitch}|{family}";
                if (RedirectLogSeen.TryAdd(redirectKey, 0))
                {
                    DiagnosticLogger.Log(Tag,
                        $"AcGiTextStyle TrueType 重定向: '{fontName}' → '{replacement}' " +
                        $"bold={bold != 0} italic={italic != 0} charset={charset} pitch={pitch} family={family}");
                }

                _inInlineFontRedirect = true;
                try
                {
                    return trampoline(self, replacementPtr, bold, italic, charset, pitch, family);
                }
                finally
                {
                    _inInlineFontRedirect = false;
                }
            }

            return trampoline(self, typeface, bold, italic, charset, pitch, family);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": AcGiTextStyle::setFont Hook 异常", ex);
            return trampoline(self, typeface, bold, italic, charset, pitch, family);
        }
        finally
        {
            _inSetFontHook = false;
        }
    }

    private static void SetFileNameHookHandler(IntPtr self, IntPtr fontName)
    {
        InvokeShxFileNameHook(
            _setFileNameHook,
            ref _inSetFileNameHook,
            self,
            fontName,
            bigFont: false,
            hookName: "AcGiTextStyle::setFileName",
            logCategory: "SHX主字体",
            inlineType: InlineFontType.ShxMain);
    }

    private static void FileNameCtorHookHandler(
        IntPtr self,
        IntPtr fontName,
        IntPtr bigFontName,
        double textSize,
        double xScale,
        double obliqueAngle,
        double trackingPercent,
        byte isBackward,
        byte isUpsideDown,
        byte isVertical,
        byte isOverlined,
        byte isUnderlined,
        byte isStrikethrough,
        IntPtr styleName)
    {
        var trampoline = _fileNameCtorHook?.TrampolineDelegate;
        if (trampoline == null)
            return;

        if (_inFileNameCtorHook || !IsInsideMTextScope || StyleTextStyleHook.IsInsideStyleRuntimeOperation)
        {
            trampoline(
                self, fontName, bigFontName, textSize, xScale, obliqueAngle, trackingPercent,
                isBackward, isUpsideDown, isVertical, isOverlined, isUnderlined, isStrikethrough, styleName);
            return;
        }

        _inFileNameCtorHook = true;
        try
        {
            IntPtr resolvedFontName = ResolveConstructorShxArgument(
                fontName,
                bigFont: false,
                logCategory: "SHX主字体",
                inlineType: InlineFontType.ShxMain,
                out _);
            IntPtr resolvedBigFontName = ResolveConstructorShxArgument(
                bigFontName,
                bigFont: true,
                logCategory: "SHX大字体",
                inlineType: InlineFontType.ShxBigFont,
                out _);

            bool redirected = resolvedFontName != IntPtr.Zero || resolvedBigFontName != IntPtr.Zero;
            if (redirected)
                _inInlineFontRedirect = true;

            try
            {
                trampoline(
                    self,
                    resolvedFontName == IntPtr.Zero ? fontName : resolvedFontName,
                    resolvedBigFontName == IntPtr.Zero ? bigFontName : resolvedBigFontName,
                    textSize,
                    xScale,
                    obliqueAngle,
                    trackingPercent,
                    isBackward,
                    isUpsideDown,
                    isVertical,
                    isOverlined,
                    isUnderlined,
                    isStrikethrough,
                    styleName);
            }
            finally
            {
                if (redirected)
                    _inInlineFontRedirect = false;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": AcGiTextStyle 构造函数 Hook 异常", ex);
            trampoline(
                self, fontName, bigFontName, textSize, xScale, obliqueAngle, trackingPercent,
                isBackward, isUpsideDown, isVertical, isOverlined, isUnderlined, isStrikethrough, styleName);
        }
        finally
        {
            _inFileNameCtorHook = false;
        }
    }

    private static void SetBigFontFileNameHookHandler(IntPtr self, IntPtr fontName)
    {
        InvokeShxFileNameHook(
            _setBigFontFileNameHook,
            ref _inSetBigFontFileNameHook,
            self,
            fontName,
            bigFont: true,
            hookName: "AcGiTextStyle::setBigFontFileName",
            logCategory: "SHX大字体",
            inlineType: InlineFontType.ShxBigFont);
    }

    private static void InvokeShxFileNameHook(
        NativeInlineHook<AcGiTextStyleSetFileNameDelegate>? hook,
        ref bool inHook,
        IntPtr self,
        IntPtr fontName,
        bool bigFont,
        string hookName,
        string logCategory,
        InlineFontType inlineType)
    {
        var trampoline = hook?.TrampolineDelegate;
        if (trampoline == null)
            return;

        if (inHook || !IsInsideMTextScope || StyleTextStyleHook.IsInsideStyleRuntimeOperation)
        {
            trampoline(self, fontName);
            return;
        }

        inHook = true;
        try
        {
            string original = Marshal.PtrToStringUni(fontName) ?? string.Empty;
            string? replacement = ResolveMissingShxReplacement(original, bigFont, out string sourceKey);
            if (replacement != null)
            {
                IntPtr replacementPtr = NativeFileNameCache.GetOrAdd(
                    replacement,
                    static name => Marshal.StringToHGlobalUni(name));

                FontRuntimeMappingStore.RecordInlineMapping(sourceKey, replacement, inlineType);

                string redirectKey = $"{hookName}|{sourceKey}|{replacement}";
                if (RedirectLogSeen.TryAdd(redirectKey, 0))
                {
                    DiagnosticLogger.Log(Tag,
                        $"{hookName} {logCategory}重定向: '{original}' → '{replacement}'");
                }

                _inInlineFontRedirect = true;
                try
                {
                    trampoline(self, replacementPtr);
                }
                finally
                {
                    _inInlineFontRedirect = false;
                }
                return;
            }

            trampoline(self, fontName);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": " + hookName + " Hook 异常", ex);
            trampoline(self, fontName);
        }
        finally
        {
            inHook = false;
        }
    }

    private static IntPtr ResolveConstructorShxArgument(
        IntPtr fontNamePtr,
        bool bigFont,
        string logCategory,
        InlineFontType inlineType,
        out string sourceKey)
    {
        sourceKey = string.Empty;
        string original = Marshal.PtrToStringUni(fontNamePtr) ?? string.Empty;
        string? replacement = ResolveMissingShxReplacement(original, bigFont, out sourceKey);
        if (replacement == null)
            return IntPtr.Zero;

        FontRuntimeMappingStore.RecordInlineMapping(sourceKey, replacement, inlineType);

        string redirectKey = $"ctor|{sourceKey}|{replacement}";
        if (RedirectLogSeen.TryAdd(redirectKey, 0))
        {
            DiagnosticLogger.Log(Tag,
                $"AcGiTextStyle 构造函数 {logCategory}重定向: '{original}' → '{replacement}'");
        }

        return NativeFileNameCache.GetOrAdd(
            replacement,
            static name => Marshal.StringToHGlobalUni(name));
    }

    private static string? ResolveMissingTrueTypeReplacement(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return null;

        string original = FontRedirectResolver.NormalizeInputName(fontName);
        bool hasAtPrefix = original.Length > 1 && original[0] == '@';
        string lookupName = hasAtPrefix ? original.TrimStart('@') : original;
        if (string.IsNullOrWhiteSpace(lookupName))
            return null;

        // AcGiTextStyle::setFont receives a typeface argument. No-extension names on
        // this path are treated as TrueType typefaces because of the API path, not
        // because the name looks like a TrueType family.
        if (IsShxFontName(lookupName))
            return null;

        if (hasAtPrefix)
        {
            if (!FontRedirectResolver.TryResolveAtPrefixedFont(
                    original,
                    FontRedirectKind.TrueType,
                    out var resolution))
            {
                return null;
            }

            return string.Equals(original, resolution.RedirectName, StringComparison.OrdinalIgnoreCase)
                ? null
                : resolution.RedirectName;
        }

        if (IsAvailableTypeface(lookupName))
            return null;

        if (!FontRedirectResolver.TryResolveConfiguredReplacement(
                FontRedirectKind.TrueType,
                out string replacement))
        {
            return null;
        }

        if (string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lookupName, replacement, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return replacement;
    }

    private static bool IsAvailableTypeface(string fontName)
    {
        return FontRedirectResolver.IsAvailableTrueType(fontName);
    }

    private static bool IsShxFontName(string fontName) =>
        fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrueTypeFontFileName(string fontName) =>
        fontName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        fontName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
        fontName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveMissingShxReplacement(
        string fontName,
        bool bigFont,
        out string sourceKey)
    {
        sourceKey = string.Empty;
        if (string.IsNullOrWhiteSpace(fontName))
            return null;

        string original = FontRedirectResolver.NormalizeInputName(fontName);
        if (string.IsNullOrWhiteSpace(original))
            return null;

        if (IsTrueTypeFontFileName(original))
            return null;

        string lookupName = original.Length > 1 && original[0] == '@'
            ? original.TrimStart('@')
            : original;
        if (string.IsNullOrWhiteSpace(lookupName))
            return null;

        var kind = bigFont ? FontRedirectKind.ShxBigFont : FontRedirectKind.ShxMain;
        string lookupKey = FontRedirectResolver.GetRedirectSourceKey(original, kind);
        sourceKey = NormalizeInlineShxName(original);

        if (original.Length > 1 && original[0] == '@')
        {
            if (!FontRedirectResolver.TryResolveAtPrefixedFont(
                    original,
                    kind,
                    out var resolution))
            {
                return null;
            }

            return string.Equals(original, resolution.RedirectName, StringComparison.OrdinalIgnoreCase)
                ? null
                : resolution.RedirectName;
        }

        if (!ShouldReplaceShx(original, lookupKey, bigFont))
            return null;

        if (!FontRedirectResolver.TryResolveConfiguredReplacement(
                kind,
                out string replacement))
        {
            return null;
        }

        if (string.Equals(lookupKey, replacement, StringComparison.OrdinalIgnoreCase))
            return null;

        return replacement;
    }

    private static bool ShouldReplaceShx(string original, string sourceKey, bool bigFont)
    {
        bool hasAtPrefix = original.Length > 1 && original[0] == '@';
        bool exactOriginalAvailable = FontAvailabilityIndex.IsExactKnownAvailableFont(original);
        bool available = (hasAtPrefix ? exactOriginalAvailable : FontAvailabilityIndex.IsKnownAvailableFont(original))
                         || FontAvailabilityIndex.IsKnownAvailableFont(sourceKey);

        // 旧版 DWG 可能把 @xxx.shx 作为真实 SHX 文件名请求；xxx.shx 存在并不代表
        // @xxx.shx 可由 AutoCAD 加载。这个场景按缺失处理，让配置字体兜底。
        if (hasAtPrefix && !exactOriginalAvailable)
            return true;

        if (!available)
            return true;

        if (FontAvailabilityIndex.TryGetKnownShxFontKind(sourceKey, out bool sourceIsBig)
            || FontAvailabilityIndex.TryGetKnownShxFontKind(original, out sourceIsBig))
        {
            return sourceIsBig != bigFont;
        }

        // 文件存在但 SHX 类型无法确认时，按缺失处理。旧图纸中这类字体
        // 常导致 MText 大字体槽位显示为空，交给配置字体兜底更稳定。
        return true;
    }

    private static string NormalizeInlineShxName(string name)
    {
        string normalized = FontRedirectResolver.NormalizeInputName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".shx";
    }

    private static void ExplodeFragmentsHookHandler(
        IntPtr self,
        IntPtr callback,
        IntPtr callbackParam,
        IntPtr worldDraw)
    {
        var trampoline = _explodeFragmentsHook?.TrampolineDelegate;
        if (trampoline == null)
            return;

        if (_inExplodeFragmentsHook || callback == IntPtr.Zero)
        {
            trampoline(self, callback, callbackParam, worldDraw);
            return;
        }

        _inExplodeFragmentsHook = true;
        _mTextScopeDepth++;
        try
        {
            trampoline(self, callback, callbackParam, worldDraw);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": AcDbMText::explodeFragments 作用域 Hook 异常", ex);
        }
        finally
        {
            if (_mTextScopeDepth > 0)
                _mTextScopeDepth--;
            _inExplodeFragmentsHook = false;
        }
    }

    private static bool TryGetExportAddress(IntPtr module, string exportName, out IntPtr address, out uint rva)
    {
        address = NativeInlineHookInterop.GetProcAddress(module, exportName);
        if (address == IntPtr.Zero)
        {
            rva = 0;
            return false;
        }

        long delta = address.ToInt64() - module.ToInt64();
        if (delta <= 0 || delta > uint.MaxValue)
        {
            rva = 0;
            return false;
        }

        rva = (uint)delta;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
