using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DBText upstream decode evidence hook.
/// <para>
/// This hook is intentionally read-only. It records the native decode context immediately before
/// AutoCAD dispatches DBText bytes into the DBCS conversion chain, so later fixes can be based on
/// native code-page evidence instead of managed text appearance.
/// </para>
/// </summary>
internal static class DbTextUpstreamDecodeProbeHook
{
    private const string Tag = "DbTextUpstreamDecode";
    private const uint MemCommit = 0x1000;
    private const int SampleLimit = 24;
    private const int ObjectInputSampleLimit = 32;
    private const int ObjectCursorDeltaStreamLimit = 512;
    private const int MaxCifInputBytes = 2048;
    private const int MaxCifOutputChars = 512;
    private const int MaxUtf16ToWideInputBytes = 4096;
    private const int MaxUtf16ToWideOutputChars = 1024;
    private const int MaxDTextFullInputBytes = 4096;
    private const string AcPalDllName = "AcPal.dll";

    private static readonly byte[] DispatcherPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x20,
        0x55,
        0x56,
        0x57,
        0x41, 0x54,
        0x41, 0x55,
        0x41, 0x56,
        0x41, 0x57
    ];

    private static readonly int[] MainDispatcherPattern =
    [
        0x48, 0x89, 0x5C, 0x24, 0x20,
        0x55,
        0x56,
        0x57,
        0x41, 0x54,
        0x41, 0x55,
        0x41, 0x56,
        0x41, 0x57,
        0x48, 0x8B, 0xEC,
        0x48, 0x83, 0xEC, 0x40,
        0x48, 0x8B, 0x05, -1, -1, -1, -1,
        0x48, 0x33, 0xC4,
        0x48, 0x89, 0x45, 0xF8,
        0x45, 0x8B, 0xA0, 0x6C, 0x04, 0x00, 0x00,
        0x4C, 0x8B, 0xE9,
        0x49, 0x8B, 0x88, 0x30, 0x04, 0x00, 0x00,
        0x49, 0x8B, 0xF8,
        0x48, 0x8B, 0xDA
    ];

    private static readonly int[] ParallelDispatcherPattern =
    [
        0x48, 0x89, 0x5C, 0x24, 0x20,
        0x55,
        0x56,
        0x57,
        0x41, 0x54,
        0x41, 0x55,
        0x41, 0x56,
        0x41, 0x57,
        0x48, 0x8B, 0xEC,
        0x48, 0x83, 0xEC, 0x40,
        0x48, 0x8B, 0x05, -1, -1, -1, -1,
        0x48, 0x33, 0xC4,
        0x48, 0x89, 0x45, 0xF8,
        0x45, 0x8B, 0xA8, 0x6C, 0x04, 0x00, 0x00,
        0x4C, 0x8B, 0xE1,
        0x49, 0x8B, 0x88, 0x30, 0x04, 0x00, 0x00,
        0x49, 0x8B, 0xF0,
        0x48, 0x8B, 0xFA
    ];

    private static NativeInlineHook<DispatcherDelegate>? _mainDispatcherHook;
    private static NativeInlineHook<DispatcherDelegate>? _parallelDispatcherHook;
    private static NativeInlineHook<MultiByteCifToWideCharDelegate>? _multiByteCifToWideCharHook;
    private static NativeInlineHook<Utf16ToWideGetWideBufferDelegate>? _utf16ToWideGetWideBufferHook;
    private static NativeInlineHook<DTextFullInputDelegate>? _dtextFullInputHook;
    private static DbTextUpstreamDecodeHookProfile? _profile;
    private static IntPtr _moduleBase;
    private static IntPtr _acPalModuleBase;
    private static bool _installed;
    private static int _mainDispatcherHitCount;
    private static int _parallelDispatcherHitCount;
    private static int _multiByteCifToWideCharHitCount;
    private static int _scopedMultiByteCifToWideCharHitCount;
    private static int _multiByteCifToWideCharNoDbTextScopeCount;
    private static int _multiByteCifToWideCharTruncatedInputCount;
    private static int _utf16ToWideGetWideBufferHitCount;
    private static int _scopedUtf16ToWideGetWideBufferHitCount;
    private static int _utf16ToWideGetWideBufferNoDbTextScopeCount;
    private static int _utf16ToWideGetWideBufferTruncatedInputCount;
    private static int _dtextFullInputHitCount;
    private static int _scopedDTextFullInputHitCount;
    private static int _dtextFullInputNoDbTextScopeCount;
    private static int _dtextFullInputTruncatedInputCount;
    private static int _scopedDispatcherHitCount;
    private static int _noDbTextScopeCount;
    private static int _codePageMismatchCount;
    private static int _invalidMemoryCount;
    private static uint _lastReturnRva;
    private static string _lastApiName = string.Empty;
    private static int _lastFilerCodePageId;
    private static int _lastContextCodePageId;
    private static int _lastBridgeCodePageId;
    private static int _lastCifCodePageId;
    private static int _lastCifMode;
    private static int _lastCifInputLength;
    private static int _lastCifReturnValue;
    private static uint _lastCifReturnRva;
    private static uint _lastUtf16ToWideReturnRva;
    private static uint _lastDTextFullInputHookRva;
    private static int _lastUtf16ToWideFilerCodePageId;
    private static int _lastDTextFullInputFilerCodePageId;
    private static long _lastUtf16ToWideCharCount;
    private static byte _lastUtf16ToWideReturnValue;
    private static byte[] _lastInputBytes = [];
    private static byte[] _lastCifInputBytes = [];
    private static byte[] _lastUtf16ToWideInputBytes = [];
    private static byte[] _lastDTextFullInputBytes = [];
    private static bool _lastDTextFullInputTruncated;
    private static char _lastOutputChar;
    private static string _lastCifOutputText = string.Empty;
    private static string _lastUtf16ToWideOutputText = string.Empty;
    private static readonly object StateLock = new();
    private static readonly Dictionary<nint, UpstreamProbeStats> StatsByImpText = new();
    private static readonly List<string> Samples = new();
    [ThreadStatic] private static bool _inHook;
    [ThreadStatic] private static bool _inCifHook;
    [ThreadStatic] private static bool _inUtf16ToWideHook;
    [ThreadStatic] private static bool _inDTextFullInputHook;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DispatcherDelegate(IntPtr owner, IntPtr cursorPointer, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MultiByteCifToWideCharDelegate(
        int codePageId,
        int mode,
        IntPtr sourceBytes,
        int sourceLength,
        IntPtr outputWideChars,
        int outputCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte Utf16ToWideGetWideBufferDelegate(
        IntPtr helper,
        IntPtr outputWideChars,
        IntPtr inOutCharCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DTextFullInputDelegate(IntPtr owner, IntPtr sourceBytes, IntPtr inputState);

    /// <summary>Install read-only upstream decode probes.</summary>
    public static void Install()
    {
        if (_installed) return;

        if (!NativeDecodeHookProfileResolver.TryGetCurrent(Tag, out var nativeProfile))
            return;

        var profile = nativeProfile.UpstreamDecode;
        _profile = profile;
        if (!profile.HasInstallableHook)
        {
            DiagnosticLogger.Log(Tag, $"{nativeProfile.PlatformName}: {profile.MultiByteCifToWideChar.DisabledReason ?? nativeProfile.SupportNote}");
            return;
        }

        _moduleBase = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (_moduleBase == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过安装。");
            return;
        }

        _acPalModuleBase = GetModuleHandle(AcPalDllName);
        if (_acPalModuleBase == IntPtr.Zero)
            DiagnosticLogger.Log(Tag, $"{AcPalDllName} 未加载，Utf16ToWide probe 将跳过安装。");

        bool mainInstalled = false;
        bool parallelInstalled = false;
        if (profile.EnableDispatcherPatterns)
        {
            mainInstalled = TryInstallDispatcher(
                "DBText main dispatcher",
                MainDispatcherPattern,
                MainDispatcherHookHandler,
                out _mainDispatcherHook);
            parallelInstalled = TryInstallDispatcher(
                "DBText parallel dispatcher",
                ParallelDispatcherPattern,
                ParallelDispatcherHookHandler,
                out _parallelDispatcherHook);
        }

        bool cifInstalled = TryInstallMultiByteCifToWideChar(profile.MultiByteCifToWideChar);
        bool utf16Installed = TryInstallUtf16ToWideGetWideBuffer(profile.Utf16ToWideGetWideBuffer);
        bool dtextFullInputInstalled = TryInstallDTextFullInput(profile.DTextFullInputProbe);

        _installed = mainInstalled || parallelInstalled || cifInstalled || utf16Installed || dtextFullInputInstalled;
        DiagnosticLogger.Log(Tag,
            $"安装完成。MainDispatcher={mainInstalled}, ParallelDispatcher={parallelInstalled}, " +
            $"MultiByteCIFToWideCharProbe={cifInstalled}, Utf16ToWideGetWideBufferProbe={utf16Installed}, " +
            $"DTextFullInputProbe={dtextFullInputInstalled}, DbcsBridge=delegated-to-TextEditorDbcsDecodeHook");
    }

    /// <summary>Uninstall upstream decode probes.</summary>
    public static void Uninstall()
    {
        _dtextFullInputHook?.Uninstall();
        _parallelDispatcherHook?.Uninstall();
        _mainDispatcherHook?.Uninstall();
        _multiByteCifToWideCharHook?.Uninstall();
        _utf16ToWideGetWideBufferHook?.Uninstall();
        _multiByteCifToWideCharHook = null;
        _utf16ToWideGetWideBufferHook = null;
        _dtextFullInputHook = null;
        _parallelDispatcherHook = null;
        _mainDispatcherHook = null;
        _profile = null;
        _moduleBase = IntPtr.Zero;
        _acPalModuleBase = IntPtr.Zero;
        lock (StateLock)
        {
            StatsByImpText.Clear();
            Samples.Clear();
        }

        _installed = false;
        DiagnosticLogger.Log(Tag,
            $"已卸载。MainDispatcherHits={_mainDispatcherHitCount}, ParallelDispatcherHits={_parallelDispatcherHitCount}, " +
            $"MultiByteCIFToWideCharHits={_multiByteCifToWideCharHitCount}, " +
            $"Utf16ToWideGetWideBufferHits={_utf16ToWideGetWideBufferHitCount}, " +
            $"DTextFullInputHits={_dtextFullInputHitCount}, Mismatches={_codePageMismatchCount}");
    }

    /// <summary>Get a global diagnostic report.</summary>
    public static string GetReport()
    {
        string sampleText;
        lock (StateLock)
        {
            sampleText = Samples.Count == 0
                ? "<none>"
                : string.Join(Environment.NewLine, Samples.Select(item => $"  {item}"));
        }

        return string.Join(Environment.NewLine,
            "=== DBText Upstream Decode Probe ===",
            $"Installed: {_installed}",
            $"MainDispatcherHitCount: {_mainDispatcherHitCount}",
            $"ParallelDispatcherHitCount: {_parallelDispatcherHitCount}",
            $"ScopedDispatcherHitCount: {_scopedDispatcherHitCount}",
            $"MultiByteCIFToWideCharHitCount: {_multiByteCifToWideCharHitCount}",
            $"ScopedMultiByteCIFToWideCharHitCount: {_scopedMultiByteCifToWideCharHitCount}",
            $"MultiByteCIFToWideCharNoDbTextScopeCount: {_multiByteCifToWideCharNoDbTextScopeCount}",
            $"MultiByteCIFToWideCharTruncatedInputCount: {_multiByteCifToWideCharTruncatedInputCount}",
            $"Utf16ToWideGetWideBufferHitCount: {_utf16ToWideGetWideBufferHitCount}",
            $"ScopedUtf16ToWideGetWideBufferHitCount: {_scopedUtf16ToWideGetWideBufferHitCount}",
            $"Utf16ToWideGetWideBufferNoDbTextScopeCount: {_utf16ToWideGetWideBufferNoDbTextScopeCount}",
            $"Utf16ToWideGetWideBufferTruncatedInputCount: {_utf16ToWideGetWideBufferTruncatedInputCount}",
            $"DTextFullInputHitCount: {_dtextFullInputHitCount}",
            $"ScopedDTextFullInputHitCount: {_scopedDTextFullInputHitCount}",
            $"DTextFullInputNoDbTextScopeCount: {_dtextFullInputNoDbTextScopeCount}",
            $"DTextFullInputTruncatedInputCount: {_dtextFullInputTruncatedInputCount}",
            "DbcsBridgeProbe: delegated-to-TextEditorDbcsDecodeHook",
            $"NoDbTextScopeCount: {_noDbTextScopeCount}",
            $"CodePageMismatchCount: {_codePageMismatchCount}",
            $"InvalidMemoryCount: {_invalidMemoryCount}",
            $"LastApiName: {_lastApiName}",
            $"LastReturnRva: 0x{_lastReturnRva:X}",
            $"LastFilerCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastFilerCodePageId)}",
            $"LastContextCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastContextCodePageId)}",
            $"LastBridgeCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastBridgeCodePageId)}",
            $"LastInputBytes: {FormatBytes(_lastInputBytes)}",
            $"LastOutputChar: {FormatChar(_lastOutputChar)}",
            $"LastCifReturnRva: 0x{_lastCifReturnRva:X}",
            $"LastCifCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastCifCodePageId)}",
            $"LastCifMode: 0x{_lastCifMode:X}",
            $"LastCifInputLength: {_lastCifInputLength}",
            $"LastCifReturnValue: {_lastCifReturnValue}",
            $"LastCifInputBytes: {FormatBytes(_lastCifInputBytes, 96)}",
            $"LastCifOutputText: {EscapeForLog(_lastCifOutputText)}",
            $"LastUtf16ToWideReturnRva: 0x{_lastUtf16ToWideReturnRva:X}",
            $"LastUtf16ToWideFilerCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastUtf16ToWideFilerCodePageId)}",
            $"LastUtf16ToWideCharCount: {_lastUtf16ToWideCharCount}",
            $"LastUtf16ToWideReturnValue: 0x{_lastUtf16ToWideReturnValue:X2}",
            $"LastUtf16ToWideInputBytes: {FormatBytes(_lastUtf16ToWideInputBytes, 128)}",
            $"LastUtf16ToWideOutputText: {EscapeForLog(_lastUtf16ToWideOutputText)}",
            $"LastDTextFullInputHookRva: 0x{_lastDTextFullInputHookRva:X}",
            $"LastDTextFullInputFilerCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastDTextFullInputFilerCodePageId)}",
            $"LastDTextFullInputBytes: {FormatBytes(_lastDTextFullInputBytes, 128)}{(_lastDTextFullInputTruncated ? " ..." : string.Empty)}",
            "Samples:",
            sampleText);
    }

    /// <summary>Try get object-level upstream probe summary for AFRINSPECTTEXT.</summary>
    public static bool TryGetProbeSummary(IntPtr impText, out NativeUpstreamDecodeProbeSummary summary)
    {
        summary = default;
        if (impText == IntPtr.Zero)
            return false;

        lock (StateLock)
        {
            if (!StatsByImpText.TryGetValue(impText, out UpstreamProbeStats? stats))
                return false;

            summary = stats.ToSummary();
            return true;
        }
    }

    private static void MainDispatcherHookHandler(IntPtr owner, IntPtr cursorPointer, IntPtr context)
    {
        Interlocked.Increment(ref _mainDispatcherHitCount);
        var trampoline = _mainDispatcherHook?.TrampolineDelegate;
        if (trampoline == null)
            return;

        if (_inHook)
        {
            trampoline(owner, cursorPointer, context);
            return;
        }

        _inHook = true;
        try
        {
            RecordDispatcher("main-dispatcher", _mainDispatcherHook?.CapturedReturnAddress ?? IntPtr.Zero, cursorPointer, context);
            trampoline(owner, cursorPointer, context);
        }
        finally
        {
            _inHook = false;
        }
    }

    private static void ParallelDispatcherHookHandler(IntPtr owner, IntPtr cursorPointer, IntPtr context)
    {
        Interlocked.Increment(ref _parallelDispatcherHitCount);
        var trampoline = _parallelDispatcherHook?.TrampolineDelegate;
        if (trampoline == null)
            return;

        if (_inHook)
        {
            trampoline(owner, cursorPointer, context);
            return;
        }

        _inHook = true;
        try
        {
            RecordDispatcher("parallel-dispatcher", _parallelDispatcherHook?.CapturedReturnAddress ?? IntPtr.Zero, cursorPointer, context);
            trampoline(owner, cursorPointer, context);
        }
        finally
        {
            _inHook = false;
        }
    }

    private static bool TryInstallDispatcher(
        string name,
        int[] pattern,
        DispatcherDelegate handler,
        out NativeInlineHook<DispatcherDelegate>? hook)
    {
        hook = null;
        if (!NativeModulePatternScanner.TryFindUniqueTextPattern(
                _moduleBase,
                pattern,
                Tag,
                name,
                out IntPtr address,
                out uint rva))
            return false;

        hook = new NativeInlineHook<DispatcherDelegate>(Tag, name, rva);
        return hook.InstallAtAddress(
            address,
            rva,
            handler,
            DispatcherPrefix.Length,
            32,
            DispatcherPrefix);
    }

    private static bool TryInstallMultiByteCifToWideChar(NativeHookTarget target)
    {
        if (!target.IsEnabled)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：{target.DisabledReason ?? "缺少导出符号"}");
            return false;
        }

        if (!TryGetExportAddress(
                _moduleBase,
                PlatformManager.Platform.AcDbDllName,
                target,
                out IntPtr address,
                out uint rva))
        {
            return false;
        }

        _multiByteCifToWideCharHook = new NativeInlineHook<MultiByteCifToWideCharDelegate>(
            Tag,
            target.Name,
            rva);
        return _multiByteCifToWideCharHook.InstallAtAddress(
            address,
            rva,
            MultiByteCifToWideCharHookHandler,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static bool TryInstallUtf16ToWideGetWideBuffer(NativeHookTarget target)
    {
        if (!target.IsEnabled)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：{target.DisabledReason ?? "缺少导出符号"}");
            return false;
        }

        if (_acPalModuleBase == IntPtr.Zero)
            return false;

        if (!TryGetExportAddress(
                _acPalModuleBase,
                AcPalDllName,
                target,
                out IntPtr address,
                out uint rva))
        {
            return false;
        }

        _utf16ToWideGetWideBufferHook = new NativeInlineHook<Utf16ToWideGetWideBufferDelegate>(
            Tag,
            target.Name,
            rva);
        return _utf16ToWideGetWideBufferHook.InstallAtAddress(
            address,
            rva,
            Utf16ToWideGetWideBufferHookHandler,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static bool TryInstallDTextFullInput(NativeHookTarget target)
    {
        if (!target.IsEnabled || !target.Rva.HasValue)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：{target.DisabledReason ?? "缺少 RVA"}");
            return false;
        }

        IntPtr address = _moduleBase + (int)target.Rva.Value;
        _dtextFullInputHook = new NativeInlineHook<DTextFullInputDelegate>(
            Tag,
            target.Name,
            target.Rva.Value);
        return _dtextFullInputHook.InstallAtAddress(
            address,
            target.Rva.Value,
            DTextFullInputHookHandler,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static int MultiByteCifToWideCharHookHandler(
        int codePageId,
        int mode,
        IntPtr sourceBytes,
        int sourceLength,
        IntPtr outputWideChars,
        int outputCapacity)
    {
        Interlocked.Increment(ref _multiByteCifToWideCharHitCount);
        var trampoline = _multiByteCifToWideCharHook?.TrampolineDelegate;
        if (trampoline == null)
            return 0;

        if (_inCifHook)
            return trampoline(codePageId, mode, sourceBytes, sourceLength, outputWideChars, outputCapacity);

        _inCifHook = true;
        byte[] inputBytes = [];
        bool inputTruncated = false;
        bool hasDbTextScope = DbTextDwgInFieldsScopeHook.TryGetCurrentDbTextScope(
            out IntPtr impText,
            out int filerCodePageId);
        IntPtr returnAddress = _multiByteCifToWideCharHook?.CapturedReturnAddress ?? IntPtr.Zero;
        int result = 0;
        bool trampolineCalled = false;
        try
        {
            if (hasDbTextScope)
            {
                Interlocked.Increment(ref _scopedMultiByteCifToWideCharHitCount);
                inputBytes = TryReadCifInputBytes(sourceBytes, sourceLength, out inputTruncated);
                if (inputTruncated)
                    Interlocked.Increment(ref _multiByteCifToWideCharTruncatedInputCount);
            }
            else
            {
                Interlocked.Increment(ref _multiByteCifToWideCharNoDbTextScopeCount);
            }

            result = trampoline(codePageId, mode, sourceBytes, sourceLength, outputWideChars, outputCapacity);
            trampolineCalled = true;
            if (hasDbTextScope)
            {
                string outputText = ReadWideString(outputWideChars, Math.Min(MaxCifOutputChars, SafePositive(outputCapacity)), out bool outputTruncated);
                RecordMultiByteCifToWideChar(
                    impText,
                    returnAddress,
                    filerCodePageId,
                    codePageId,
                    mode,
                    sourceLength,
                    inputBytes,
                    inputTruncated,
                    result,
                    outputText,
                    outputTruncated);
            }

            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            DiagnosticLogger.LogError(Tag + ": MultiByteCIFToWideCharHookHandler 异常", ex);
            return trampolineCalled
                ? result
                : trampoline(codePageId, mode, sourceBytes, sourceLength, outputWideChars, outputCapacity);
        }
        finally
        {
            _inCifHook = false;
        }
    }

    private static byte Utf16ToWideGetWideBufferHookHandler(
        IntPtr helper,
        IntPtr outputWideChars,
        IntPtr inOutCharCount)
    {
        Interlocked.Increment(ref _utf16ToWideGetWideBufferHitCount);
        var trampoline = _utf16ToWideGetWideBufferHook?.TrampolineDelegate;
        if (trampoline == null)
            return 0;

        if (_inUtf16ToWideHook)
            return trampoline(helper, outputWideChars, inOutCharCount);

        _inUtf16ToWideHook = true;
        bool hasDbTextScope = DbTextDwgInFieldsScopeHook.TryGetCurrentDbTextScope(
            out IntPtr impText,
            out int filerCodePageId);
        IntPtr returnAddress = _utf16ToWideGetWideBufferHook?.CapturedReturnAddress ?? IntPtr.Zero;
        byte result = 0;
        bool trampolineCalled = false;
        try
        {
            IntPtr sourceUtf16 = IntPtr.Zero;
            long sourceCharCount = 0;
            byte[] inputBytes = [];
            bool inputTruncated = false;
            if (hasDbTextScope)
            {
                Interlocked.Increment(ref _scopedUtf16ToWideGetWideBufferHitCount);
                sourceUtf16 = TryReadIntPtr(helper);
                sourceCharCount = TryReadNativeInt64(helper + IntPtr.Size);
                inputBytes = TryReadUtf16SourceBytes(sourceUtf16, sourceCharCount, out inputTruncated);
                if (inputTruncated)
                    Interlocked.Increment(ref _utf16ToWideGetWideBufferTruncatedInputCount);
            }
            else
            {
                Interlocked.Increment(ref _utf16ToWideGetWideBufferNoDbTextScopeCount);
            }

            result = trampoline(helper, outputWideChars, inOutCharCount);
            trampolineCalled = true;
            if (hasDbTextScope)
            {
                long outputCharCount = TryReadNativeInt64(inOutCharCount);
                string outputText = ReadWideString(
                    outputWideChars,
                    Math.Min(MaxUtf16ToWideOutputChars, SafePositiveLong(outputCharCount)),
                    out bool outputTruncated);
                RecordUtf16ToWideGetWideBuffer(
                    impText,
                    returnAddress,
                    filerCodePageId,
                    sourceCharCount,
                    inputBytes,
                    inputTruncated,
                    result,
                    outputText,
                    outputTruncated,
                    sourceUtf16 == outputWideChars);
            }

            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            DiagnosticLogger.LogError(Tag + ": Utf16ToWideGetWideBufferHookHandler 异常", ex);
            return trampolineCalled
                ? result
                : trampoline(helper, outputWideChars, inOutCharCount);
        }
        finally
        {
            _inUtf16ToWideHook = false;
        }
    }

    private static void DTextFullInputHookHandler(IntPtr owner, IntPtr sourceBytes, IntPtr inputState)
    {
        Interlocked.Increment(ref _dtextFullInputHitCount);
        var trampoline = _dtextFullInputHook?.TrampolineDelegate;
        if (trampoline == null)
            return;

        if (_inDTextFullInputHook)
        {
            if (sourceBytes != IntPtr.Zero)
                trampoline(owner, sourceBytes, inputState);
            return;
        }

        _inDTextFullInputHook = true;
        bool trampolineCalled = false;
        try
        {
            if (sourceBytes == IntPtr.Zero)
            {
                trampolineCalled = true;
                return;
            }

            if (DbTextDwgInFieldsScopeHook.TryGetCurrentDbTextScope(out IntPtr impText, out int filerCodePageId))
            {
                Interlocked.Increment(ref _scopedDTextFullInputHitCount);
                byte[] inputBytes = TryReadNullTerminatedBytes(sourceBytes, MaxDTextFullInputBytes, out bool truncated);
                if (truncated)
                    Interlocked.Increment(ref _dtextFullInputTruncatedInputCount);

                RecordDTextFullInput(impText, filerCodePageId, inputBytes, truncated);
            }
            else
            {
                Interlocked.Increment(ref _dtextFullInputNoDbTextScopeCount);
            }

            trampoline(owner, sourceBytes, inputState);
            trampolineCalled = true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            DiagnosticLogger.LogError(Tag + ": DTextFullInputHookHandler 异常", ex);
            if (!trampolineCalled)
                trampoline(owner, sourceBytes, inputState);
        }
        finally
        {
            _inDTextFullInputHook = false;
        }
    }

    private static void RecordDispatcher(string apiName, IntPtr returnAddress, IntPtr cursorPointer, IntPtr context)
    {
        if (!DbTextDwgInFieldsScopeHook.TryGetCurrentDbTextScope(out IntPtr impText, out int filerCodePageId))
        {
            Interlocked.Increment(ref _noDbTextScopeCount);
            return;
        }

        Interlocked.Increment(ref _scopedDispatcherHitCount);
        int contextCodePageId = TryReadInt32(context + 0x46C);
        IntPtr textState = TryReadIntPtr(context + 0x430);
        ushort firstFamilyChar = TryReadUInt16(textState + 0x18);
        IntPtr cursor = TryReadIntPtr(cursorPointer);
        byte[] bytes = TryReadBytes(cursor, 12);
        UpdateLast(apiName, returnAddress, filerCodePageId, contextCodePageId, 0, bytes, '\0');
        bool mismatch = IsMeaningfulCodePage(filerCodePageId)
            && IsMeaningfulCodePage(contextCodePageId)
            && filerCodePageId != contextCodePageId;
        if (mismatch)
            Interlocked.Increment(ref _codePageMismatchCount);

        string sample =
            $"{apiName} ret=0x{GetReturnRva(returnAddress):X} imp=0x{impText.ToInt64():X} " +
            $"filer={DwgFilerCodePageScopeHook.FormatCodePageId(filerCodePageId)} " +
            $"context={DwgFilerCodePageScopeHook.FormatCodePageId(contextCodePageId)} " +
            $"firstFamily=U+{firstFamilyChar:X4} bytes={FormatBytes(bytes)}";

        RecordStats(
            impText,
            apiName,
            returnAddress,
            filerCodePageId,
            contextCodePageId,
            0,
            bytes,
            '\0',
            cursor,
            isDispatcher: true,
            mismatch,
            sample);

        AddSample(sample);
    }

    private static void RecordStats(
        IntPtr impText,
        string apiName,
        IntPtr returnAddress,
        int filerCodePageId,
        int contextCodePageId,
        int bridgeCodePageId,
        byte[] inputBytes,
        char output,
        IntPtr inputCursor,
        bool isDispatcher,
        bool mismatch,
        string? sample = null)
    {
        lock (StateLock)
        {
            if (!StatsByImpText.TryGetValue(impText, out UpstreamProbeStats? stats))
            {
                stats = new UpstreamProbeStats();
                StatsByImpText[impText] = stats;
            }

            stats.LastApiName = apiName;
            stats.LastReturnRva = GetReturnRva(returnAddress);
            stats.LastFilerCodePageId = filerCodePageId;
            if (contextCodePageId != 0)
                stats.LastContextCodePageId = contextCodePageId;
            if (bridgeCodePageId != 0)
                stats.LastBridgeCodePageId = bridgeCodePageId;
            stats.LastInputBytes = inputBytes;
            stats.LastOutputChar = output;
            if (isDispatcher)
            {
                stats.DispatcherHitCount++;
                stats.RecordDispatcherCursor(inputCursor, inputBytes);
            }
            else
            {
                stats.DbcsBridgeHitCount++;
            }

            if (mismatch)
                stats.CodePageMismatchCount++;
            if (sample != null && stats.InputSamples.Count < ObjectInputSampleLimit)
                stats.InputSamples.Add(sample);
        }
    }

    private static void RecordMultiByteCifToWideChar(
        IntPtr impText,
        IntPtr returnAddress,
        int filerCodePageId,
        int codePageId,
        int mode,
        int sourceLength,
        byte[] inputBytes,
        bool inputTruncated,
        int returnValue,
        string outputText,
        bool outputTruncated)
    {
        uint returnRva = GetReturnRva(returnAddress);
        _lastCifReturnRva = returnRva;
        _lastCifCodePageId = codePageId;
        _lastCifMode = mode;
        _lastCifInputLength = sourceLength;
        _lastCifReturnValue = returnValue;
        _lastCifInputBytes = inputBytes;
        _lastCifOutputText = outputText;

        string sample =
            $"cif-to-wide ret=0x{returnRva:X} imp=0x{impText.ToInt64():X} " +
            $"filer={DwgFilerCodePageScopeHook.FormatCodePageId(filerCodePageId)} " +
            $"argCp={DwgFilerCodePageScopeHook.FormatCodePageId(codePageId)} " +
            $"mode=0x{mode:X} len={sourceLength} captured={inputBytes.Length}{(inputTruncated ? "+" : "")} " +
            $"rv={returnValue} outLen={outputText.Length}{(outputTruncated ? "+" : "")} " +
            $"bytes={FormatBytes(inputBytes, 48)} out='{EscapeForLog(outputText)}'";

        lock (StateLock)
        {
            if (!StatsByImpText.TryGetValue(impText, out UpstreamProbeStats? stats))
            {
                stats = new UpstreamProbeStats();
                StatsByImpText[impText] = stats;
            }

            stats.CifToWideCharHitCount++;
            stats.LastCifReturnRva = returnRva;
            stats.LastCifFilerCodePageId = filerCodePageId;
            stats.LastCifCodePageId = codePageId;
            stats.LastCifMode = mode;
            stats.LastCifInputLength = sourceLength;
            stats.LastCifInputBytes = inputBytes.ToArray();
            stats.LastCifInputTruncated = inputTruncated;
            stats.LastCifReturnValue = returnValue;
            stats.LastCifOutputText = outputText;
            stats.LastCifOutputTruncated = outputTruncated;
            if (stats.InputSamples.Count < ObjectInputSampleLimit)
                stats.InputSamples.Add(sample);
        }

        AddSample(sample);
    }

    private static void RecordUtf16ToWideGetWideBuffer(
        IntPtr impText,
        IntPtr returnAddress,
        int filerCodePageId,
        long sourceCharCount,
        byte[] inputBytes,
        bool inputTruncated,
        byte returnValue,
        string outputText,
        bool outputTruncated,
        bool sourceEqualsOutput)
    {
        uint returnRva = GetReturnRva(returnAddress);
        _lastUtf16ToWideReturnRva = returnRva;
        _lastUtf16ToWideFilerCodePageId = filerCodePageId;
        _lastUtf16ToWideCharCount = sourceCharCount;
        _lastUtf16ToWideReturnValue = returnValue;
        _lastUtf16ToWideInputBytes = inputBytes;
        _lastUtf16ToWideOutputText = outputText;

        string sample =
            $"utf16-to-wide ret=0x{returnRva:X} imp=0x{impText.ToInt64():X} " +
            $"filer={DwgFilerCodePageScopeHook.FormatCodePageId(filerCodePageId)} " +
            $"chars={sourceCharCount} bytes={inputBytes.Length}{(inputTruncated ? "+" : "")} " +
            $"rv=0x{returnValue:X2} sourceEqualsOutput={sourceEqualsOutput} " +
            $"outLen={outputText.Length}{(outputTruncated ? "+" : "")} " +
            $"bytes={FormatBytes(inputBytes, 64)} out='{EscapeForLog(outputText)}'";

        lock (StateLock)
        {
            if (!StatsByImpText.TryGetValue(impText, out UpstreamProbeStats? stats))
            {
                stats = new UpstreamProbeStats();
                StatsByImpText[impText] = stats;
            }

            stats.Utf16ToWideGetWideBufferHitCount++;
            stats.LastUtf16ToWideReturnRva = returnRva;
            stats.LastUtf16ToWideFilerCodePageId = filerCodePageId;
            stats.LastUtf16ToWideCharCount = sourceCharCount;
            stats.LastUtf16ToWideReturnValue = returnValue;
            stats.LastUtf16ToWideInputBytes = inputBytes.ToArray();
            stats.LastUtf16ToWideInputTruncated = inputTruncated;
            stats.LastUtf16ToWideOutputText = outputText;
            stats.LastUtf16ToWideOutputTruncated = outputTruncated;
            stats.LastUtf16ToWideSourceEqualsOutput = sourceEqualsOutput;
            if (stats.InputSamples.Count < ObjectInputSampleLimit)
                stats.InputSamples.Add(sample);
        }

        AddSample(sample);
    }

    private static void RecordDTextFullInput(
        IntPtr impText,
        int filerCodePageId,
        byte[] inputBytes,
        bool inputTruncated)
    {
        uint hookRva = _profile?.DTextFullInputProbe.Rva ?? 0;
        _lastDTextFullInputHookRva = hookRva;
        _lastDTextFullInputFilerCodePageId = filerCodePageId;
        _lastDTextFullInputBytes = inputBytes;
        _lastDTextFullInputTruncated = inputTruncated;

        string sample =
            $"dtext-full-input hook=0x{hookRva:X} imp=0x{impText.ToInt64():X} " +
            $"filer={DwgFilerCodePageScopeHook.FormatCodePageId(filerCodePageId)} " +
            $"len={inputBytes.Length}{(inputTruncated ? "+" : "")} bytes={FormatBytes(inputBytes, 64)}";

        lock (StateLock)
        {
            if (!StatsByImpText.TryGetValue(impText, out UpstreamProbeStats? stats))
            {
                stats = new UpstreamProbeStats();
                StatsByImpText[impText] = stats;
            }

            stats.DTextFullInputHitCount++;
            stats.LastDTextFullInputHookRva = hookRva;
            stats.LastDTextFullInputFilerCodePageId = filerCodePageId;
            stats.LastDTextFullInputBytes = inputBytes.ToArray();
            stats.LastDTextFullInputTruncated = inputTruncated;
            if (stats.InputSamples.Count < ObjectInputSampleLimit)
                stats.InputSamples.Add(sample);
        }

        AddSample(sample);
    }

    private static void UpdateLast(
        string apiName,
        IntPtr returnAddress,
        int filerCodePageId,
        int contextCodePageId,
        int bridgeCodePageId,
        byte[] inputBytes,
        char output)
    {
        _lastApiName = apiName;
        _lastReturnRva = GetReturnRva(returnAddress);
        _lastFilerCodePageId = filerCodePageId;
        if (contextCodePageId != 0)
            _lastContextCodePageId = contextCodePageId;
        if (bridgeCodePageId != 0)
            _lastBridgeCodePageId = bridgeCodePageId;
        _lastInputBytes = inputBytes;
        _lastOutputChar = output;
    }

    private static void AddSample(string sample)
    {
        lock (StateLock)
        {
            if (Samples.Count >= SampleLimit)
                return;

            Samples.Add(sample);
        }
    }

    private static bool IsMeaningfulCodePage(int codePageId)
    {
        return codePageId is >= 0x20 and <= 0x80;
    }

    private static IntPtr TryReadIntPtr(IntPtr address)
    {
        try
        {
            return address != IntPtr.Zero && IsCommittedMemory(address)
                ? Marshal.ReadIntPtr(address)
                : IntPtr.Zero;
        }
        catch
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            return IntPtr.Zero;
        }
    }

    private static int TryReadInt32(IntPtr address)
    {
        try
        {
            return address != IntPtr.Zero && IsCommittedMemory(address)
                ? Marshal.ReadInt32(address)
                : 0;
        }
        catch
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            return 0;
        }
    }

    private static long TryReadNativeInt64(IntPtr address)
    {
        try
        {
            return address != IntPtr.Zero && IsCommittedMemory(address)
                ? Marshal.ReadInt64(address)
                : 0;
        }
        catch
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            return 0;
        }
    }

    private static ushort TryReadUInt16(IntPtr address)
    {
        try
        {
            return address != IntPtr.Zero && IsCommittedMemory(address)
                ? (ushort)Marshal.ReadInt16(address)
                : (ushort)0;
        }
        catch
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            return 0;
        }
    }

    private static char TryReadWideChar(IntPtr address)
    {
        try
        {
            return address != IntPtr.Zero && IsCommittedMemory(address)
                ? (char)Marshal.ReadInt16(address)
                : '\0';
        }
        catch
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            return '\0';
        }
    }

    private static byte[] TryReadBytes(IntPtr address, int maxCount)
    {
        if (address == IntPtr.Zero || maxCount <= 0)
            return [];

        try
        {
            var bytes = new byte[maxCount];
            int count = 0;
            for (; count < maxCount; count++)
            {
                IntPtr current = address + count;
                if (!IsCommittedMemory(current))
                    break;

                bytes[count] = Marshal.ReadByte(current);
            }

            if (count != bytes.Length)
                Array.Resize(ref bytes, count);
            return bytes;
        }
        catch
        {
            Interlocked.Increment(ref _invalidMemoryCount);
            return [];
        }
    }

    private static byte[] TryReadCifInputBytes(IntPtr address, int sourceLength, out bool truncated)
    {
        truncated = false;
        if (address == IntPtr.Zero)
            return [];

        int maxCount;
        if (sourceLength >= 0)
        {
            maxCount = sourceLength;
        }
        else
        {
            maxCount = MaxCifInputBytes;
        }

        if (maxCount <= 0)
            return [];

        int captureCount = Math.Min(maxCount, MaxCifInputBytes);
        byte[] bytes = TryReadBytes(address, captureCount);
        if (sourceLength >= 0)
        {
            truncated = sourceLength > bytes.Length;
            return bytes;
        }

        int nullIndex = Array.IndexOf(bytes, (byte)0);
        if (nullIndex >= 0)
        {
            if (nullIndex != bytes.Length)
                Array.Resize(ref bytes, nullIndex);
            return bytes;
        }

        truncated = bytes.Length >= MaxCifInputBytes;
        return bytes;
    }

    private static byte[] TryReadUtf16SourceBytes(IntPtr address, long sourceCharCount, out bool truncated)
    {
        truncated = false;
        if (address == IntPtr.Zero || sourceCharCount <= 0)
            return [];

        long byteCount = sourceCharCount > (long.MaxValue / 2)
            ? long.MaxValue
            : sourceCharCount * 2;
        int captureCount = (int)Math.Min(byteCount, MaxUtf16ToWideInputBytes);
        byte[] bytes = TryReadBytes(address, captureCount);
        truncated = byteCount > bytes.Length;
        return bytes;
    }

    private static byte[] TryReadNullTerminatedBytes(IntPtr address, int maxCount, out bool truncated)
    {
        truncated = false;
        if (address == IntPtr.Zero || maxCount <= 0)
            return [];

        byte[] bytes = TryReadBytes(address, maxCount);
        int nullIndex = Array.IndexOf(bytes, (byte)0);
        if (nullIndex >= 0)
        {
            if (nullIndex != bytes.Length)
                Array.Resize(ref bytes, nullIndex);
            return bytes;
        }

        truncated = bytes.Length >= maxCount;
        return bytes;
    }

    private static string ReadWideString(IntPtr address, int maxChars, out bool truncated)
    {
        truncated = false;
        if (address == IntPtr.Zero || maxChars <= 0 || !IsCommittedMemory(address))
            return string.Empty;

        try
        {
            var chars = new List<char>(Math.Min(maxChars, 128));
            for (int i = 0; i < maxChars; i++)
            {
                IntPtr current = address + (i * 2);
                if (!IsCommittedMemory(current))
                    break;

                char ch = (char)Marshal.ReadInt16(current);
                if (ch == '\0')
                    return new string(chars.ToArray());

                chars.Add(ch);
            }

            truncated = chars.Count >= maxChars;
            return new string(chars.ToArray());
        }
        catch
        {
            truncated = false;
            return string.Empty;
        }
    }

    private static int SafePositive(int value)
    {
        return value > 0 ? value : MaxCifOutputChars;
    }

    private static int SafePositiveLong(long value)
    {
        return value is > 0 and <= int.MaxValue
            ? (int)value
            : MaxUtf16ToWideOutputChars;
    }

    private static uint GetReturnRva(IntPtr returnAddress)
    {
        return GetRva(_moduleBase, returnAddress);
    }

    private static uint GetRva(IntPtr moduleBase, IntPtr address)
    {
        if (address == IntPtr.Zero || moduleBase == IntPtr.Zero)
            return 0;

        long rva = address.ToInt64() - moduleBase.ToInt64();
        if (rva <= 0 || rva > uint.MaxValue)
            return 0;

        return (uint)rva;
    }

    private static bool IsCommittedMemory(IntPtr address)
    {
        try
        {
            return NativeInlineHookInterop.VirtualQuery(
                    address,
                    out NativeInlineHookInterop.MemoryBasicInformation info,
                    (uint)Marshal.SizeOf<NativeInlineHookInterop.MemoryBasicInformation>())
                != IntPtr.Zero
                && info.State == MemCommit;
        }
        catch { return false; }
    }

    private static string FormatBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "<empty>";

        return string.Join(" ", bytes.Select(value => value.ToString("X2")));
    }

    private static string FormatBytes(byte[] bytes, int limit)
    {
        if (bytes.Length == 0)
            return "<empty>";

        int count = Math.Min(bytes.Length, limit);
        var parts = new string[count];
        for (int i = 0; i < count; i++)
            parts[i] = bytes[i].ToString("X2");

        return bytes.Length <= limit
            ? string.Join(" ", parts)
            : string.Join(" ", parts) + " ...";
    }

    private static string FormatChar(char value)
    {
        return value == '\0'
            ? "<none>"
            : $"U+{(int)value:X4} '{value}'";
    }

    private static bool TryGetExportAddress(
        IntPtr moduleBase,
        string moduleName,
        NativeHookTarget target,
        out IntPtr address,
        out uint actualRva)
    {
        address = IntPtr.Zero;
        actualRva = 0;
        if (moduleBase == IntPtr.Zero)
            return false;

        string? exportName = target.ExportName;
        if (!target.IsEnabled || string.IsNullOrWhiteSpace(exportName))
        {
            DiagnosticLogger.Log(Tag, $"{moduleName}!{target.Name} 未启用：{target.DisabledReason ?? "缺少导出符号"}");
            return false;
        }

        address = NativeInlineHookInterop.GetProcAddress(moduleBase, exportName!);
        if (address == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{moduleName}!{target.Name} 导出缺失，跳过安装。");
            return false;
        }

        actualRva = GetRva(moduleBase, address);
        if (target.Rva.HasValue && actualRva != target.Rva.Value)
        {
            DiagnosticLogger.Log(Tag,
                $"{moduleName}!{target.Name} RVA 不匹配，expected=0x{target.Rva.Value:X}, actual=0x{actualRva:X}，跳过安装。");
            address = IntPtr.Zero;
            actualRva = 0;
            return false;
        }

        return true;
    }

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("'", "\\'");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private sealed class UpstreamProbeStats
    {
        public int DispatcherHitCount { get; set; }
        public int DbcsBridgeHitCount { get; set; }
        public int CifToWideCharHitCount { get; set; }
        public int Utf16ToWideGetWideBufferHitCount { get; set; }
        public int DTextFullInputHitCount { get; set; }
        public int CodePageMismatchCount { get; set; }
        public int LastFilerCodePageId { get; set; }
        public int LastContextCodePageId { get; set; }
        public int LastBridgeCodePageId { get; set; }
        public int LastCifFilerCodePageId { get; set; }
        public int LastCifCodePageId { get; set; }
        public int LastCifMode { get; set; }
        public int LastCifInputLength { get; set; }
        public int LastCifReturnValue { get; set; }
        public int LastUtf16ToWideFilerCodePageId { get; set; }
        public int LastDTextFullInputFilerCodePageId { get; set; }
        public long LastUtf16ToWideCharCount { get; set; }
        public byte LastUtf16ToWideReturnValue { get; set; }
        public string LastApiName { get; set; } = string.Empty;
        public uint LastReturnRva { get; set; }
        public uint LastCifReturnRva { get; set; }
        public uint LastUtf16ToWideReturnRva { get; set; }
        public uint LastDTextFullInputHookRva { get; set; }
        public byte[] LastInputBytes { get; set; } = [];
        public byte[] LastCifInputBytes { get; set; } = [];
        public bool LastCifInputTruncated { get; set; }
        public byte[] LastUtf16ToWideInputBytes { get; set; } = [];
        public bool LastUtf16ToWideInputTruncated { get; set; }
        public byte[] LastDTextFullInputBytes { get; set; } = [];
        public bool LastDTextFullInputTruncated { get; set; }
        public char LastOutputChar { get; set; }
        public string LastCifOutputText { get; set; } = string.Empty;
        public bool LastCifOutputTruncated { get; set; }
        public string LastUtf16ToWideOutputText { get; set; } = string.Empty;
        public bool LastUtf16ToWideOutputTruncated { get; set; }
        public bool LastUtf16ToWideSourceEqualsOutput { get; set; }
        public List<string> InputSamples { get; } = [];
        public List<byte> CursorDeltaStreamBytes { get; } = [];
        public bool CursorDeltaStreamTruncated { get; private set; }
        public int CursorDeltaStreamAmbiguousCount { get; private set; }

        private IntPtr LastDispatcherCursor { get; set; }
        private byte[] PendingDispatcherInputBytes { get; set; } = [];
        private bool HasPendingDispatcherInput { get; set; }

        public void RecordDispatcherCursor(IntPtr cursor, byte[] inputBytes)
        {
            if (cursor == IntPtr.Zero || inputBytes.Length == 0)
            {
                CursorDeltaStreamAmbiguousCount++;
                return;
            }

            if (HasPendingDispatcherInput && LastDispatcherCursor != IntPtr.Zero)
            {
                long delta = cursor.ToInt64() - LastDispatcherCursor.ToInt64();
                if (delta > 0 && delta <= PendingDispatcherInputBytes.Length && delta <= 16)
                {
                    AppendCursorDeltaBytes(PendingDispatcherInputBytes, (int)delta);
                }
                else if (delta != 0)
                {
                    CursorDeltaStreamAmbiguousCount++;
                }
            }

            LastDispatcherCursor = cursor;
            PendingDispatcherInputBytes = inputBytes.ToArray();
            HasPendingDispatcherInput = true;
        }

        private void AppendCursorDeltaBytes(byte[] bytes, int count)
        {
            if (CursorDeltaStreamBytes.Count >= ObjectCursorDeltaStreamLimit)
            {
                CursorDeltaStreamTruncated = true;
                return;
            }

            int take = Math.Min(count, ObjectCursorDeltaStreamLimit - CursorDeltaStreamBytes.Count);
            for (int i = 0; i < take; i++)
                CursorDeltaStreamBytes.Add(bytes[i]);

            if (take < count)
                CursorDeltaStreamTruncated = true;
        }

        public NativeUpstreamDecodeProbeSummary ToSummary()
        {
            return new NativeUpstreamDecodeProbeSummary(
                DispatcherHitCount,
                DbcsBridgeHitCount,
                CifToWideCharHitCount,
                Utf16ToWideGetWideBufferHitCount,
                DTextFullInputHitCount,
                CodePageMismatchCount,
                LastFilerCodePageId,
                LastContextCodePageId,
                LastBridgeCodePageId,
                LastCifFilerCodePageId,
                LastCifCodePageId,
                LastCifMode,
                LastCifInputLength,
                LastCifReturnValue,
                LastUtf16ToWideFilerCodePageId,
                LastDTextFullInputFilerCodePageId,
                LastUtf16ToWideCharCount,
                LastUtf16ToWideReturnValue,
                LastApiName,
                LastReturnRva,
                LastCifReturnRva,
                LastUtf16ToWideReturnRva,
                LastDTextFullInputHookRva,
                LastInputBytes.ToArray(),
                LastCifInputBytes.ToArray(),
                LastCifInputTruncated,
                LastUtf16ToWideInputBytes.ToArray(),
                LastUtf16ToWideInputTruncated,
                LastDTextFullInputBytes.ToArray(),
                LastDTextFullInputTruncated,
                LastOutputChar,
                LastCifOutputText,
                LastCifOutputTruncated,
                LastUtf16ToWideOutputText,
                LastUtf16ToWideOutputTruncated,
                LastUtf16ToWideSourceEqualsOutput,
                CursorDeltaStreamBytes.ToArray(),
                CursorDeltaStreamTruncated,
                CursorDeltaStreamAmbiguousCount,
                HasPendingDispatcherInput ? PendingDispatcherInputBytes.ToArray() : [],
                InputSamples.ToArray());
        }
    }
}

internal readonly record struct NativeUpstreamDecodeProbeSummary(
    int DispatcherHitCount,
    int DbcsBridgeHitCount,
    int CifToWideCharHitCount,
    int Utf16ToWideGetWideBufferHitCount,
    int DTextFullInputHitCount,
    int CodePageMismatchCount,
    int LastFilerCodePageId,
    int LastContextCodePageId,
    int LastBridgeCodePageId,
    int LastCifFilerCodePageId,
    int LastCifCodePageId,
    int LastCifMode,
    int LastCifInputLength,
    int LastCifReturnValue,
    int LastUtf16ToWideFilerCodePageId,
    int LastDTextFullInputFilerCodePageId,
    long LastUtf16ToWideCharCount,
    byte LastUtf16ToWideReturnValue,
    string LastApiName,
    uint LastReturnRva,
    uint LastCifReturnRva,
    uint LastUtf16ToWideReturnRva,
    uint LastDTextFullInputHookRva,
    byte[] LastInputBytes,
    byte[] LastCifInputBytes,
    bool LastCifInputTruncated,
    byte[] LastUtf16ToWideInputBytes,
    bool LastUtf16ToWideInputTruncated,
    byte[] LastDTextFullInputBytes,
    bool LastDTextFullInputTruncated,
    char LastOutputChar,
    string LastCifOutputText,
    bool LastCifOutputTruncated,
    string LastUtf16ToWideOutputText,
    bool LastUtf16ToWideOutputTruncated,
    bool LastUtf16ToWideSourceEqualsOutput,
    byte[] CursorDeltaStreamBytes,
    bool CursorDeltaStreamTruncated,
    int CursorDeltaStreamAmbiguousCount,
    byte[] CursorDeltaPendingBytes,
    string[] InputSamples);
