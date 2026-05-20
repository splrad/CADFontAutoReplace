using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
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
    private static readonly ConcurrentDictionary<string, InlineFontType> InlineFontCandidates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> LoadStyleRecRedirectLogSeen = new(StringComparer.OrdinalIgnoreCase);
    private static long _explodeFragmentsHitCount;
    private static long _explodeFragmentsCallbackHitCount;
    private static long _explodeFragmentsWorldDrawHitCount;
    private static long _loadStyleRecInlineHitCount;
    private static long _loadStyleRecInlineApplyCount;
    private static long _setFontHitCount;
    private static long _setFileNameHitCount;
    private static long _setBigFontFileNameHitCount;
    private static long _fileNameCtorHitCount;
    private static long _suppressedSetterHitCount;

    [ThreadStatic] private static bool _inSetFontHook;
    [ThreadStatic] private static bool _inFileNameCtorHook;
    [ThreadStatic] private static bool _inSetFileNameHook;
    [ThreadStatic] private static bool _inSetBigFontFileNameHook;
    [ThreadStatic] private static bool _inInlineFontRedirect;
    [ThreadStatic] private static bool _inExplodeFragmentsHook;
    [ThreadStatic] private static bool _suppressInlineRuntimeMapping;
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

    internal static IDisposable SuppressInlineRuntimeMapping()
        => new InlineRuntimeMappingSuppressionScope();

    internal static string GetDiagnosticsSummary()
        => $"ExplodeFragmentsHits={Interlocked.Read(ref _explodeFragmentsHitCount)}, "
           + $"CallbackHits={Interlocked.Read(ref _explodeFragmentsCallbackHitCount)}, "
           + $"WorldDrawHits={Interlocked.Read(ref _explodeFragmentsWorldDrawHitCount)}, "
           + $"LoadStyleRecInlineHits={Interlocked.Read(ref _loadStyleRecInlineHitCount)}, "
           + $"LoadStyleRecInlineApplies={Interlocked.Read(ref _loadStyleRecInlineApplyCount)}, "
           + $"SetFontHits={Interlocked.Read(ref _setFontHitCount)}, "
           + $"SetFileNameHits={Interlocked.Read(ref _setFileNameHitCount)}, "
           + $"SetBigFontFileNameHits={Interlocked.Read(ref _setBigFontFileNameHitCount)}, "
           + $"CtorHits={Interlocked.Read(ref _fileNameCtorHitCount)}, "
           + $"SuppressedSetterHits={Interlocked.Read(ref _suppressedSetterHitCount)}";

    internal static void ReplaceInlineFontCandidates(IReadOnlyDictionary<string, InlineFontType> inlineFonts)
    {
        InlineFontCandidates.Clear();

        foreach (var (fontName, inlineType) in inlineFonts)
        {
            string key = inlineType == InlineFontType.TrueType
                ? MTextFontParser.NormalizeTrueTypeFontName(fontName)
                : NormalizeInlineShxName(fontName);

            if (!string.IsNullOrWhiteSpace(key))
                InlineFontCandidates[key] = inlineType;
        }
    }

    internal static bool TryApplyInlineLoadStyleRecMappings(
        IntPtr self,
        string styleName,
        string fileName,
        string bigFontFileName,
        Func<string, IntPtr> getNativeString,
        Action<IntPtr, IntPtr>? setFileName,
        Action<IntPtr, IntPtr>? setBigFontFileName)
    {
        if (!IsInsideInlineFontHook)
            return false;

        if (!string.IsNullOrWhiteSpace(styleName) || InlineFontCandidates.IsEmpty)
            return false;

        bool hasCandidate = IsInlineCandidate(fileName, InlineFontType.ShxMain)
                            || IsInlineCandidate(bigFontFileName, InlineFontType.ShxBigFont);
        if (!hasCandidate)
            return false;

        Interlocked.Increment(ref _loadStyleRecInlineHitCount);
        bool applied = false;

        applied |= TryApplyInlineLoadStyleRecShxMapping(
            self,
            fileName,
            bigFont: false,
            InlineFontType.ShxMain,
            getNativeString,
            setFileName,
            "SHX主字体");

        applied |= TryApplyInlineLoadStyleRecShxMapping(
            self,
            bigFontFileName,
            bigFont: true,
            InlineFontType.ShxBigFont,
            getNativeString,
            setBigFontFileName,
            "SHX大字体");

        if (applied)
            Interlocked.Increment(ref _loadStyleRecInlineApplyCount);

        return applied;
    }

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
        DiagnosticLogger.Log(Tag,
            "已卸载。"
            + GetDiagnosticsSummary());

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
        LoadStyleRecRedirectLogSeen.Clear();
        InlineFontCandidates.Clear();
        Interlocked.Exchange(ref _explodeFragmentsHitCount, 0);
        Interlocked.Exchange(ref _explodeFragmentsCallbackHitCount, 0);
        Interlocked.Exchange(ref _explodeFragmentsWorldDrawHitCount, 0);
        Interlocked.Exchange(ref _loadStyleRecInlineHitCount, 0);
        Interlocked.Exchange(ref _loadStyleRecInlineApplyCount, 0);
        Interlocked.Exchange(ref _setFontHitCount, 0);
        Interlocked.Exchange(ref _setFileNameHitCount, 0);
        Interlocked.Exchange(ref _setBigFontFileNameHitCount, 0);
        Interlocked.Exchange(ref _fileNameCtorHitCount, 0);
        Interlocked.Exchange(ref _suppressedSetterHitCount, 0);
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

        string scopedFontName = ReadNativeString(typeface);
        bool inlineCandidate = IsInlineCandidate(scopedFontName, InlineFontType.TrueType);
        if (_inSetFontHook
            || _suppressInlineRuntimeMapping
            || (!IsInsideMTextScope && !inlineCandidate)
            || (StyleTextStyleHook.IsInsideStyleRuntimeOperation && !inlineCandidate))
        {
            if (_suppressInlineRuntimeMapping)
                Interlocked.Increment(ref _suppressedSetterHitCount);

            return trampoline(self, typeface, bold, italic, charset, pitch, family);
        }

        Interlocked.Increment(ref _setFontHitCount);
        _inSetFontHook = true;
        try
        {
            string fontName = scopedFontName;
            if (TryRegisterLdFileTrueTypeMapping(
                    fontName,
                    "AcGiTextStyle::setFont:TrueType",
                    out _))
            {
                return trampoline(self, typeface, bold, italic, charset, pitch, family);
            }

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

        bool inlineCandidate = IsInlineCandidate(ReadNativeString(fontName), InlineFontType.ShxMain)
                               || IsInlineCandidate(ReadNativeString(bigFontName), InlineFontType.ShxBigFont);
        if (_inFileNameCtorHook
            || _suppressInlineRuntimeMapping
            || (!IsInsideMTextScope && !inlineCandidate)
            || (StyleTextStyleHook.IsInsideStyleRuntimeOperation && !inlineCandidate))
        {
            if (_suppressInlineRuntimeMapping)
                Interlocked.Increment(ref _suppressedSetterHitCount);

            trampoline(
                self, fontName, bigFontName, textSize, xScale, obliqueAngle, trackingPercent,
                isBackward, isUpsideDown, isVertical, isOverlined, isUnderlined, isStrikethrough, styleName);
            return;
        }

        Interlocked.Increment(ref _fileNameCtorHitCount);
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

        string originalForScope = ReadNativeString(fontName);
        bool inlineCandidate = IsInlineCandidate(originalForScope, inlineType);
        if (inHook
            || _suppressInlineRuntimeMapping
            || (!IsInsideMTextScope && !inlineCandidate)
            || (StyleTextStyleHook.IsInsideStyleRuntimeOperation && !inlineCandidate))
        {
            if (_suppressInlineRuntimeMapping)
                Interlocked.Increment(ref _suppressedSetterHitCount);

            trampoline(self, fontName);
            return;
        }

        if (bigFont)
            Interlocked.Increment(ref _setBigFontFileNameHitCount);
        else
            Interlocked.Increment(ref _setFileNameHitCount);

        inHook = true;
        try
        {
            string original = originalForScope;
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

            if (TryRegisterLdFileShxMapping(
                    original,
                    bigFont,
                    inlineType,
                    $"{hookName}:{logCategory}",
                    out _))
            {
                trampoline(self, fontName);
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
        {
            TryRegisterLdFileShxMapping(
                original,
                bigFont,
                inlineType,
                $"AcGiTextStyle.ctor:{logCategory}",
                out sourceKey);
            return IntPtr.Zero;
        }

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

        string original = MTextFontParser.NormalizeTrueTypeFontName(fontName);
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
            return null;

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

        // @-prefixed SHX names carry AutoCAD big-font/vertical semantics for MText.
        // Do not replace the AcGiTextStyle name here; let ldfile bridge only the
        // backing font file so the original inline decoding path stays intact.
        if (original.Length > 1 && original[0] == '@')
            return null;

        var kind = bigFont ? FontRedirectKind.ShxBigFont : FontRedirectKind.ShxMain;
        string lookupKey = FontRedirectResolver.GetRedirectSourceKey(original, kind);
        sourceKey = NormalizeInlineShxName(original);

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

    private static bool TryRegisterLdFileTrueTypeMapping(
        string fontName,
        string source,
        out string sourceKey)
    {
        sourceKey = string.Empty;
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        string original = MTextFontParser.NormalizeTrueTypeFontName(fontName);
        if (string.IsNullOrWhiteSpace(original) || original.Length <= 1 || original[0] != '@')
            return false;

        string lookupName = original.TrimStart('@');
        if (string.IsNullOrWhiteSpace(lookupName) || IsShxFontName(lookupName))
            return false;

        if (FontRedirectResolver.TryResolveAvailableBase(
                lookupName,
                FontRedirectKind.TrueType,
                out _))
        {
            return false;
        }

        return LdFileHook.TryRegisterResolvedAtFont(
            original,
            FontRedirectKind.TrueType,
            source,
            InlineFontType.TrueType,
            out sourceKey,
            out _);
    }

    private static bool TryRegisterLdFileShxMapping(
        string fontName,
        bool bigFont,
        InlineFontType inlineType,
        string source,
        out string sourceKey)
    {
        sourceKey = string.Empty;
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        string input = FontRedirectResolver.NormalizeInputName(fontName);
        if (string.IsNullOrWhiteSpace(input) || IsTrueTypeFontFileName(input))
            return false;

        string original = NormalizeInlineShxName(input);
        if (string.IsNullOrWhiteSpace(original) || original.Length <= 1 || original[0] != '@')
            return false;

        if (IsExactAvailableShxForSlot(original, bigFont))
            return false;

        var kind = bigFont ? FontRedirectKind.ShxBigFont : FontRedirectKind.ShxMain;
        return LdFileHook.TryRegisterResolvedAtFont(
            original,
            kind,
            source,
            inlineType,
            out sourceKey,
            out _);
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

    private static bool IsExactAvailableShxForSlot(string original, bool bigFont)
    {
        if (!FontAvailabilityIndex.IsExactKnownAvailableFont(original))
            return false;

        return FontAvailabilityIndex.TryGetKnownShxFontKind(original, out bool sourceIsBig)
               && sourceIsBig == bigFont;
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

    private static bool IsInlineCandidate(string fontName, InlineFontType expectedType)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        string key = expectedType == InlineFontType.TrueType
            ? MTextFontParser.NormalizeTrueTypeFontName(fontName)
            : NormalizeInlineShxName(fontName);

        return InlineFontCandidates.TryGetValue(key, out InlineFontType actualType)
               && actualType == expectedType;
    }

    private static string ReadNativeString(IntPtr value)
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

    private static bool TryApplyInlineLoadStyleRecShxMapping(
        IntPtr self,
        string original,
        bool bigFont,
        InlineFontType inlineType,
        Func<string, IntPtr> getNativeString,
        Action<IntPtr, IntPtr>? setter,
        string logCategory)
    {
        if (!IsInlineCandidate(original, inlineType))
            return false;

        string? replacement = ResolveMissingShxReplacement(original, bigFont, out string sourceKey);
        if (replacement == null)
        {
            return TryRegisterLdFileShxMapping(
                original,
                bigFont,
                inlineType,
                $"AcGiTextStyle.loadStyleRec:{logCategory}",
                out _);
        }

        if (setter == null)
            return false;

        setter(self, getNativeString(replacement));
        FontRuntimeMappingStore.RecordInlineMapping(sourceKey, replacement, inlineType);

        string redirectKey = $"loadStyleRec|{sourceKey}|{replacement}|{logCategory}";
        if (LoadStyleRecRedirectLogSeen.TryAdd(redirectKey, 0))
        {
            DiagnosticLogger.Log(Tag,
                $"AcGiTextStyle.loadStyleRec MText内联{logCategory}重定向: '{original}' → '{replacement}'");
        }

        return true;
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

        if (_inExplodeFragmentsHook)
        {
            trampoline(self, callback, callbackParam, worldDraw);
            return;
        }

        Interlocked.Increment(ref _explodeFragmentsHitCount);
        if (callback != IntPtr.Zero)
            Interlocked.Increment(ref _explodeFragmentsCallbackHitCount);
        if (worldDraw != IntPtr.Zero)
            Interlocked.Increment(ref _explodeFragmentsWorldDrawHitCount);

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

    private sealed class InlineRuntimeMappingSuppressionScope : IDisposable
    {
        private readonly bool _previous;

        internal InlineRuntimeMappingSuppressionScope()
        {
            _previous = _suppressInlineRuntimeMapping;
            _suppressInlineRuntimeMapping = true;
        }

        public void Dispose()
        {
            _suppressInlineRuntimeMapping = _previous;
        }
    }
}
