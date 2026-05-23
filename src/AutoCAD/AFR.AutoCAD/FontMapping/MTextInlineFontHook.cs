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
    private static AcGiTextStyleSetFontDelegate? _setFontHookDelegate;
    private static AcGiTextStyleFileNameCtorDelegate? _fileNameCtorHookDelegate;
    private static AcGiTextStyleSetFileNameDelegate? _setFileNameHookDelegate;
    private static AcGiTextStyleSetFileNameDelegate? _setBigFontFileNameHookDelegate;
    private static readonly ConcurrentDictionary<string, InlineFontCandidate> InlineFontCandidates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, InlineFontCandidate?> FoldedInlineFontCandidates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> FoldedCandidateAmbiguityLogSeen = new(StringComparer.Ordinal);
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
    [ThreadStatic] private static bool _suppressInlineRuntimeMapping;

    internal static bool IsInsideInlineFontHook
    {
        get
        {
            return _inInlineFontRedirect;
        }
    }

    internal static bool IsInstalled =>
        _setFontHook?.IsInstalled == true
        || _fileNameCtorHook?.IsInstalled == true
        || _setFileNameHook?.IsInstalled == true
        || _setBigFontFileNameHook?.IsInstalled == true;

    internal static IDisposable SuppressInlineRuntimeMapping()
        => new InlineRuntimeMappingSuppressionScope();

    internal static string GetDiagnosticsSummary()
        => $"SetFontHits={Interlocked.Read(ref _setFontHitCount)}, "
           + $"SetFileNameHits={Interlocked.Read(ref _setFileNameHitCount)}, "
           + $"SetBigFontFileNameHits={Interlocked.Read(ref _setBigFontFileNameHitCount)}, "
           + $"CtorHits={Interlocked.Read(ref _fileNameCtorHitCount)}, "
           + $"SuppressedSetterHits={Interlocked.Read(ref _suppressedSetterHitCount)}";

    internal static void ClearInlineFontCandidates()
    {
        InlineFontCandidates.Clear();
        FoldedInlineFontCandidates.Clear();
        FoldedCandidateAmbiguityLogSeen.Clear();
    }

    internal static void ResetDiagnosticsCounters()
    {
        Interlocked.Exchange(ref _setFontHitCount, 0);
        Interlocked.Exchange(ref _setFileNameHitCount, 0);
        Interlocked.Exchange(ref _setBigFontFileNameHitCount, 0);
        Interlocked.Exchange(ref _fileNameCtorHitCount, 0);
        Interlocked.Exchange(ref _suppressedSetterHitCount, 0);
    }

    internal static void ReplaceInlineFontCandidates(IReadOnlyDictionary<string, InlineFontCandidate> inlineFonts)
    {
        ClearInlineFontCandidates();

        foreach (var candidate in inlineFonts.Values)
        {
            string key = GetInlineCandidateKey(candidate.OriginalFont, candidate.FontType);

            if (!string.IsNullOrWhiteSpace(key))
            {
                var normalizedCandidate = candidate with { LookupName = key };
                InlineFontCandidates[key] = normalizedCandidate;
                AddFoldedInlineCandidate(key, normalizedCandidate);
            }
        }
    }

    internal static int PreRegisterRuntimeRequests()
    {
        if (InlineFontCandidates.IsEmpty)
            return 0;

        int registered = 0;
        foreach (InlineFontCandidate candidate in InlineFontCandidates.Values)
        {
            bool ok = candidate.FontType switch
            {
                InlineFontType.TrueType => TryRegisterTrueTypeMapping(
                    candidate.LookupName,
                    candidate.OriginalFont,
                    "MTextInlineFontScanner:preRegen:TrueType",
                    out _),
                InlineFontType.ShxBigFont => TryRegisterShxMapping(
                    candidate.LookupName,
                    candidate.OriginalFont,
                    bigFont: true,
                    candidate.FontType,
                    "MTextInlineFontScanner:preRegen:SHX大字体",
                    out _),
                _ => TryRegisterShxMapping(
                    candidate.LookupName,
                    candidate.OriginalFont,
                    bigFont: false,
                    candidate.FontType,
                    "MTextInlineFontScanner:preRegen:SHX主字体",
                    out _)
            };

            if (ok)
            {
                registered++;
            }
        }

        if (registered > 0)
        {
            DiagnosticLogger.Log(Tag,
                $"MText 内联缺失字体已在 Regen 前登记文件级映射: {registered}项");
        }

        return registered;
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

        NativeFontHookProfile profile = exports.NativeFontHookProfile;
        TryInstallSetFontHook(module, profile.AcGiTextStyleSetFont);
        TryInstallFileNameCtorHook(module, profile.AcGiTextStyleFileNameCtor);
        TryInstallSetFileNameHook(module, profile.AcGiTextStyleSetFileName);
        TryInstallSetBigFontFileNameHook(module, profile.AcGiTextStyleSetBigFontFileName);
    }

    internal static void Uninstall()
    {
        DiagnosticLogger.Log(Tag,
            "已卸载。"
            + GetDiagnosticsSummary());

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
        ClearInlineFontCandidates();
        ResetDiagnosticsCounters();
    }

    private static void TryInstallSetFontHook(IntPtr module, NativeHookTarget target)
    {
        if (_setFontHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, target, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::setFont 导出未找到，跳过 MText 内联 TrueType Hook。");
            return;
        }

        _setFontHookDelegate = SetFontHookHandler;
        _setFontHook = new NativeInlineHook<AcGiTextStyleSetFontDelegate>(
            Tag,
            target.Name,
            target.Rva ?? rva);

        _setFontHook.InstallAtAddress(
            address,
            rva,
            _setFontHookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static void TryInstallFileNameCtorHook(IntPtr module, NativeHookTarget target)
    {
        if (_fileNameCtorHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, target, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::AcGiTextStyle(font,bigFont) 导出未找到，跳过构造函数 Hook。");
            return;
        }

        _fileNameCtorHookDelegate = FileNameCtorHookHandler;
        _fileNameCtorHook = new NativeInlineHook<AcGiTextStyleFileNameCtorDelegate>(
            Tag,
            target.Name,
            target.Rva ?? rva);

        _fileNameCtorHook.InstallAtAddress(
            address,
            rva,
            _fileNameCtorHookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static void TryInstallSetFileNameHook(IntPtr module, NativeHookTarget target)
    {
        if (_setFileNameHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, target, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::setFileName 导出未找到，跳过 MText 内联 SHX 主字体 Hook。");
            return;
        }

        _setFileNameHookDelegate = SetFileNameHookHandler;
        _setFileNameHook = new NativeInlineHook<AcGiTextStyleSetFileNameDelegate>(
            Tag,
            target.Name,
            target.Rva ?? rva);

        _setFileNameHook.InstallAtAddress(
            address,
            rva,
            _setFileNameHookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static void TryInstallSetBigFontFileNameHook(IntPtr module, NativeHookTarget target)
    {
        if (_setBigFontFileNameHook?.IsInstalled == true)
            return;

        if (!TryGetExportAddress(module, target, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::setBigFontFileName 导出未找到，跳过 MText 内联 SHX 大字体 Hook。");
            return;
        }

        _setBigFontFileNameHookDelegate = SetBigFontFileNameHookHandler;
        _setBigFontFileNameHook = new NativeInlineHook<AcGiTextStyleSetFileNameDelegate>(
            Tag,
            target.Name,
            target.Rva ?? rva);

        _setBigFontFileNameHook.InstallAtAddress(
            address,
            rva,
            _setBigFontFileNameHookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
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

        if (_inSetFontHook
            || _suppressInlineRuntimeMapping
            || StyleTextStyleHook.IsInsideStyleRuntimeOperation
            || InlineFontCandidates.IsEmpty)
        {
            if (_suppressInlineRuntimeMapping)
                Interlocked.Increment(ref _suppressedSetterHitCount);

            return trampoline(self, typeface, bold, italic, charset, pitch, family);
        }

        string scopedFontName = ReadNativeString(typeface);
        bool inlineCandidate = TryGetInlineCandidate(
            scopedFontName,
            InlineFontType.TrueType,
            out InlineFontCandidate? candidate);
        if (!inlineCandidate)
            return trampoline(self, typeface, bold, italic, charset, pitch, family);

        Interlocked.Increment(ref _setFontHitCount);
        _inSetFontHook = true;
        try
        {
            string fontName = scopedFontName;
            string displayFontName = candidate?.OriginalFont ?? fontName;
            if (TryRegisterTrueTypeMapping(
                    fontName,
                    displayFontName,
                    "AcGiTextStyle::setFont:TrueType",
                    out _))
            {
                _inInlineFontRedirect = true;
                try
                {
                    return trampoline(self, typeface, bold, italic, charset, pitch, family);
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

        if (_inFileNameCtorHook
            || _suppressInlineRuntimeMapping
            || StyleTextStyleHook.IsInsideStyleRuntimeOperation
            || InlineFontCandidates.IsEmpty)
        {
            if (_suppressInlineRuntimeMapping)
                Interlocked.Increment(ref _suppressedSetterHitCount);

            trampoline(
                self, fontName, bigFontName, textSize, xScale, obliqueAngle, trackingPercent,
                isBackward, isUpsideDown, isVertical, isOverlined, isUnderlined, isStrikethrough, styleName);
            return;
        }

        bool inlineCandidate = IsInlineCandidate(ReadNativeString(fontName), InlineFontType.ShxMain)
                               || IsInlineCandidate(ReadNativeString(bigFontName), InlineFontType.ShxBigFont);
        if (!inlineCandidate)
        {
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

        if (inHook
            || _suppressInlineRuntimeMapping
            || StyleTextStyleHook.IsInsideStyleRuntimeOperation
            || InlineFontCandidates.IsEmpty)
        {
            if (_suppressInlineRuntimeMapping)
                Interlocked.Increment(ref _suppressedSetterHitCount);

            trampoline(self, fontName);
            return;
        }

        string originalForScope = ReadNativeString(fontName);
        bool inlineCandidate = TryGetInlineCandidate(
            originalForScope,
            inlineType,
            out InlineFontCandidate? candidate);
        if (!inlineCandidate)
        {
            trampoline(self, fontName);
            return;
        }

        if (bigFont)
        {
            Interlocked.Increment(ref _setBigFontFileNameHitCount);
        }
        else
        {
            Interlocked.Increment(ref _setFileNameHitCount);
        }

        inHook = true;
        try
        {
            string original = originalForScope;
            string displayOriginal = candidate?.OriginalFont ?? original;
            if (TryRegisterShxMapping(
                    original,
                    displayOriginal,
                    bigFont,
                    inlineType,
                    $"{hookName}:{logCategory}",
                    out _))
            {
                _inInlineFontRedirect = true;
                try
                {
                    trampoline(self, fontName);
                    return;
                }
                finally
                {
                    _inInlineFontRedirect = false;
                }
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
        TryGetInlineCandidate(original, inlineType, out InlineFontCandidate? candidate);
        string displayOriginal = candidate?.OriginalFont ?? original;
        TryRegisterShxMapping(
            original,
            displayOriginal,
            bigFont,
            inlineType,
            $"AcGiTextStyle.ctor:{logCategory}",
            out sourceKey);
        return IntPtr.Zero;
    }

    private static bool IsShxFontName(string fontName) =>
        fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrueTypeFontFileName(string fontName) =>
        fontName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        fontName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
        fontName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    private static bool TryRegisterTrueTypeMapping(
        string fontName,
        string displayFontName,
        string source,
        out string sourceKey)
    {
        sourceKey = string.Empty;
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        string original = MTextFontParser.NormalizeTrueTypeFontName(fontName);
        if (string.IsNullOrWhiteSpace(original))
            return false;

        string lookupName = original.TrimStart('@');
        if (string.IsNullOrWhiteSpace(lookupName) || IsShxFontName(lookupName))
            return false;

        FontLogicalReplacement resolution = FontRedirectResolver.ResolveLogicalFont(
            original,
            FontRedirectKind.TrueType,
            preserveOriginalLoadRequest: true);
        if (resolution.Action != FontLogicalReplacementAction.RuntimeLoadBridge)
            return false;

        return FontRuntimeRequestRegistry.TryRegisterResolvedRequest(
            original,
            FontRedirectKind.TrueType,
            source,
            "多行文字",
            InlineFontType.TrueType,
            displayFontName,
            out sourceKey,
            out _);
    }

    private static bool TryRegisterShxMapping(
        string fontName,
        string displayFontName,
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
        if (string.IsNullOrWhiteSpace(original))
            return false;
        var kind = bigFont ? FontRedirectKind.ShxBigFont : FontRedirectKind.ShxMain;
        FontLogicalReplacement resolution = FontRedirectResolver.ResolveLogicalFont(
            original,
            kind,
            preserveOriginalLoadRequest: true);
        if (resolution.Action != FontLogicalReplacementAction.RuntimeLoadBridge)
            return false;

        return FontRuntimeRequestRegistry.TryRegisterResolvedRequest(
            original,
            kind,
            source,
            "多行文字",
            inlineType,
            displayFontName,
            out sourceKey,
            out _);
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
        => TryGetInlineCandidate(fontName, expectedType, out _);

    private static bool TryGetInlineCandidate(
        string fontName,
        InlineFontType expectedType,
        out InlineFontCandidate? candidate)
    {
        candidate = null;
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        string key = GetInlineCandidateKey(fontName, expectedType);
        if (InlineFontCandidates.TryGetValue(key, out candidate)
            && candidate.FontType == expectedType)
        {
            return true;
        }

        if (expectedType == InlineFontType.TrueType)
        {
            candidate = null;
            return false;
        }

        string foldedKey = GetFoldedInlineCandidateKey(key, expectedType);
        if (FoldedInlineFontCandidates.TryGetValue(foldedKey, out candidate)
            && candidate is { FontType: var actualType }
            && actualType == expectedType)
        {
            return true;
        }

        if (FoldedInlineFontCandidates.ContainsKey(foldedKey))
            LogFoldedCandidateAmbiguity(key, expectedType);

        candidate = null;
        return false;
    }

    private static string GetInlineCandidateKey(string fontName, InlineFontType expectedType)
    {
        return expectedType == InlineFontType.TrueType
            ? MTextFontParser.NormalizeTrueTypeFontName(fontName)
            : NormalizeInlineShxName(fontName);
    }

    private static void AddFoldedInlineCandidate(string key, InlineFontCandidate candidate)
    {
        if (candidate.FontType == InlineFontType.TrueType)
            return;

        string foldedKey = GetFoldedInlineCandidateKey(key, candidate.FontType);
        FoldedInlineFontCandidates.AddOrUpdate(
            foldedKey,
            candidate,
            (_, existing) => existing != null && string.Equals(existing.LookupName, candidate.LookupName, StringComparison.OrdinalIgnoreCase)
                ? existing
                : null);
    }

    private static string GetFoldedInlineCandidateKey(string key, InlineFontType fontType)
        => string.Concat(fontType, "\u001F", key.ToUpperInvariant());

    private static void LogFoldedCandidateAmbiguity(string key, InlineFontType fontType)
    {
        string logKey = string.Concat("folded-ambiguous|", fontType, "|", key.ToUpperInvariant());
        if (FoldedCandidateAmbiguityLogSeen.TryAdd(logKey, 0))
        {
            DiagnosticLogger.Log(Tag,
                $"MText 内联字体回调大小写恢复存在歧义，已跳过: kind={fontType} request='{key}'");
        }
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

    private static bool TryGetExportAddress(IntPtr module, NativeHookTarget target, out IntPtr address, out uint rva)
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
