#if DEBUG
using System.Runtime.InteropServices;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DBText DWG 反序列化 code page 作用域 Hook。
/// <para>
/// 单行文字的 DBCS 解码会在 <c>AcDbImpText::dwgInFields</c> 内部触发，但不一定处于
/// <c>AcDbMemoryDwgFiler::readString</c> 的短作用域内。本 Hook 只在 AcDbText 的 native
/// 读字段窗口中压入 DWG filer 自身的 code page，让下游 TextEditor DBCS hook 在真实解码点
/// 使用图纸声明的 code page，而不是 session-wide 系统 locale。
/// </para>
/// </summary>
internal static class DbTextDwgInFieldsScopeHook
{
    private const string Tag = "DbTextDwgInScope";
    private const uint AcDbImpTextDwgInFieldsRva = 0x49910;
    private const int ImpTextStringConstVtableOffset = 0x560;
    private const uint MemCommit = 0x1000;
    private const int ProvenanceLogLimit = 80;

    private static readonly byte[] AcDbImpTextDwgInFieldsPrefix =
    [
        0x40, 0x55,
        0x56,
        0x57,
        0x41, 0x54,
        0x41, 0x55,
        0x41, 0x56,
        0x41, 0x57,
        0x48, 0x81, 0xEC, 0x00, 0x02, 0x00, 0x00
    ];

    private static NativeInlineHook<DwgInFieldsDelegate>? _hook;
    private static IntPtr _moduleBase;
    private static bool _installed;
    private static int _hitCount;
    private static int _scopedHitCount;
    private static int _noScopeHitCount;
    private static int _errorCount;
    private static int _provenanceCount;
    private static int _nativeDbcsDecodedCharCount;
    private static int _provenanceWithoutDbcsSequenceCount;
    private static int _provenanceLogCount;
    private static uint _lastReturnRva;
    private static readonly object ProvenanceLock = new();
    private static readonly Dictionary<nint, NativeDbTextProvenance> ProvenanceByImpText = new();
    [ThreadStatic] private static bool _inHook;
    [ThreadStatic] private static IntPtr _currentImpText;
    [ThreadStatic] private static List<char>? _currentDecodedDbcsChars;

    /// <summary>
    /// <c>AcDbImpText::dwgInFields(AcDbDwgFiler*)</c> 原生委托。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DwgInFieldsDelegate(IntPtr impText, IntPtr filer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ImpTextStringConstDelegate(IntPtr impText);

    /// <summary>安装 DBText DWG 读字段作用域 Hook。</summary>
    public static void Install()
    {
        if (_installed) return;

        if (!IsSupportedPlatform())
            return;

        _moduleBase = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (_moduleBase == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过安装。");
            return;
        }

        _hook = new NativeInlineHook<DwgInFieldsDelegate>(
            Tag,
            "AcDbImpText.dwgInFields",
            AcDbImpTextDwgInFieldsRva);
        _installed = _hook.Install(
            _moduleBase,
            HookHandler,
            AcDbImpTextDwgInFieldsPrefix.Length,
            64,
            AcDbImpTextDwgInFieldsPrefix);
    }

    /// <summary>卸载 DBText DWG 读字段作用域 Hook。</summary>
    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook = null;
        _moduleBase = IntPtr.Zero;
        lock (ProvenanceLock)
        {
            ProvenanceByImpText.Clear();
        }

        _installed = false;
        DiagnosticLogger.Log(Tag,
            $"已卸载。HitCount={_hitCount}, ScopedHitCount={_scopedHitCount}, " +
            $"NoScopeHitCount={_noScopeHitCount}, ProvenanceCount={_provenanceCount}, ErrorCount={_errorCount}");
    }

    /// <summary>获取诊断报告。</summary>
    public static string GetReport()
    {
        return string.Join(Environment.NewLine,
            "=== DBText DWG In Fields Scope Hook ===",
            $"Installed: {_installed}",
            $"HitCount: {_hitCount}",
            $"ScopedHitCount: {_scopedHitCount}",
            $"NoScopeHitCount: {_noScopeHitCount}",
            $"ProvenanceCount: {_provenanceCount}",
            $"NativeDbcsDecodedCharCount: {_nativeDbcsDecodedCharCount}",
            $"ProvenanceWithoutDbcsSequenceCount: {_provenanceWithoutDbcsSequenceCount}",
            $"ErrorCount: {_errorCount}",
            $"LastReturnRva: 0x{_lastReturnRva:X}");
    }

    /// <summary>
    /// 尝试读取 native DBText 读入阶段记录的对象级证据。
    /// </summary>
    public static bool TryGetProvenance(IntPtr impText, out NativeDbTextProvenance provenance)
    {
        if (impText == IntPtr.Zero)
        {
            provenance = default;
            return false;
        }

        lock (ProvenanceLock)
        {
            return ProvenanceByImpText.TryGetValue(impText, out provenance);
        }
    }

    /// <summary>
    /// 记录当前 DBText 读入作用域内 native DBCS 解码得到的字符。
    /// </summary>
    public static void RecordNativeDecodedDbcsChar(char value)
    {
        if (value == '\0' || _currentImpText == IntPtr.Zero)
            return;

        _currentDecodedDbcsChars?.Add(value);
        Interlocked.Increment(ref _nativeDbcsDecodedCharCount);
    }

    private static bool IsSupportedPlatform()
    {
        if (!PlatformManager.Platform.AcDbDllName.Equals("acdb25.dll", StringComparison.OrdinalIgnoreCase)
            || !PlatformManager.Platform.VersionName.Equals("2025", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticLogger.Log(Tag,
                $"{PlatformManager.Platform.DisplayName} 未验证 AcDbImpText::dwgInFields RVA，跳过安装。");
            return false;
        }

        return true;
    }

    private static int HookHandler(IntPtr impText, IntPtr filer)
    {
        var trampoline = _hook?.TrampolineDelegate;
        if (trampoline == null)
            return 0;

        if (_inHook)
            return trampoline(impText, filer);

        _inHook = true;
        IDisposable? scope = null;
        IntPtr previousImpText = _currentImpText;
        List<char>? previousDecodedDbcsChars = _currentDecodedDbcsChars;
        _currentImpText = impText;
        _currentDecodedDbcsChars = new List<char>();
        try
        {
            Interlocked.Increment(ref _hitCount);
            _lastReturnRva = GetReturnRva(_hook?.CapturedReturnAddress ?? IntPtr.Zero);

            scope = DwgFilerCodePageScopeHook.EnterExternalScope(
                filer,
                "AcDbImpText.dwgInFields",
                _lastReturnRva);
            if (scope == null)
                Interlocked.Increment(ref _noScopeHitCount);
            else
                Interlocked.Increment(ref _scopedHitCount);

            int status = trampoline(impText, filer);
            if (status == 0 && scope != null)
                TryRememberProvenance(impText, filer, new string(_currentDecodedDbcsChars.ToArray()));

            return status;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            DiagnosticLogger.LogError(Tag + ": HookHandler 异常", ex);
            return trampoline(impText, filer);
        }
        finally
        {
            scope?.Dispose();
            _currentImpText = previousImpText;
            _currentDecodedDbcsChars = previousDecodedDbcsChars;
            _inHook = false;
        }
    }

    private static void TryRememberProvenance(IntPtr impText, IntPtr filer, string nativeDbcsDecodedText)
    {
        try
        {
            if (impText == IntPtr.Zero || !IsCommittedMemory(impText))
                return;

            if (!DwgFilerCodePageScopeHook.TryGetCurrentDbcsCodePageId(out int codePageId))
                return;

            string nativeText = ReadImpTextString(impText);
            if (string.IsNullOrEmpty(nativeText))
                return;

            var provenance = new NativeDbTextProvenance(
                impText,
                filer,
                codePageId,
                nativeText,
                nativeDbcsDecodedText);
            if (nativeDbcsDecodedText.Length == 0)
                Interlocked.Increment(ref _provenanceWithoutDbcsSequenceCount);

            lock (ProvenanceLock)
            {
                ProvenanceByImpText[impText] = provenance;
            }

            Interlocked.Increment(ref _provenanceCount);
            if (Interlocked.Increment(ref _provenanceLogCount) <= ProvenanceLogLimit)
            {
                DiagnosticLogger.Log(Tag,
                    $"对象证据: impText=0x{impText.ToInt64():X}, filer=0x{filer.ToInt64():X}, " +
                    $"codePage={DwgFilerCodePageScopeHook.FormatCodePageId(codePageId)}, " +
                    $"dbcsDecodedChars={nativeDbcsDecodedText.Length}, " +
                    $"text='{EscapeForLog(nativeText)}'");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            DiagnosticLogger.LogError(Tag + ": 记录对象级证据失败", ex);
        }
    }

    private static string ReadImpTextString(IntPtr impText)
    {
        if (!IsCommittedMemory(impText))
            return string.Empty;

        IntPtr vtable = Marshal.ReadIntPtr(impText);
        if (vtable == IntPtr.Zero || !IsCommittedMemory(vtable + ImpTextStringConstVtableOffset))
            return string.Empty;

        IntPtr getter = Marshal.ReadIntPtr(vtable + ImpTextStringConstVtableOffset);
        if (getter == IntPtr.Zero || !IsCommittedMemory(getter))
            return string.Empty;

        var getterDelegate = Marshal.GetDelegateForFunctionPointer<ImpTextStringConstDelegate>(getter);
        IntPtr textPtr = getterDelegate(impText);
        if (textPtr == IntPtr.Zero || !IsCommittedMemory(textPtr))
            return string.Empty;

        return Marshal.PtrToStringUni(textPtr) ?? string.Empty;
    }

    private static uint GetReturnRva(IntPtr returnAddress)
    {
        if (returnAddress == IntPtr.Zero || _moduleBase == IntPtr.Zero)
            return 0;

        long rva = returnAddress.ToInt64() - _moduleBase.ToInt64();
        if (rva <= 0 || rva > uint.MaxValue)
            return 0;

        return (uint)rva;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, uint dwLength);

    private static bool IsCommittedMemory(IntPtr address)
    {
        try
        {
            return VirtualQuery(address, out MemoryBasicInformation info, (uint)Marshal.SizeOf<MemoryBasicInformation>()) != IntPtr.Zero
                && info.State == MemCommit;
        }
        catch { return false; }
    }

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}

/// <summary>
/// native DBText 读入阶段记录的对象级证据。
/// </summary>
internal readonly record struct NativeDbTextProvenance(
    IntPtr ImpText,
    IntPtr Filer,
    int CodePageId,
    string NativeText,
    string NativeDbcsDecodedText);
#endif
