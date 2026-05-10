#if DEBUG
using System.Runtime.InteropServices;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DWG 字符串读取 code page 作用域 Hook。
/// <para>
/// 通过 hook <c>AcDbMemoryDwgFiler::readString</c> 的两个重载，在 AutoCAD 原生解码字符串期间
/// 从 DWG filer 读取真实 <c>code_page_id</c>，供 <see cref="CodePageFamilyHook"/> 修正解码上下文使用。
/// </para>
/// </summary>
internal static class DwgFilerCodePageScopeHook
{
    private const string Tag = "DwgCodePageScope";
    private const string ReadStringAcStringExport = "?readString@AcDbMemoryDwgFiler@@UEAA?AW4ErrorStatus@Acad@@AEAVAcString@@@Z";
    private const string ReadStringWideCharPointerExport = "?readString@AcDbMemoryDwgFiler@@UEAA?AW4ErrorStatus@Acad@@PEAPEA_W@Z";
    private const string GetFilerCodePageIdExport = "?acdbGetFilerCodePageId@@YA?AW4code_page_id@@PEAVAcDbDwgFiler@@@Z";
    private const string CodePageIdIsDoubleByteExport = "?CodePageIdIsDoubleByte@AcCodePage@@SA_NW4code_page_id@@@Z";
    private const uint MemCommit = 0x1000;
    private const int ScopeLogLimit = 40;
    private const int StringLogLimit = 240;
    private const int MaxLoggedStringChars = 160;

    private static readonly byte[] ReadStringAcStringPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x18, 0x55, 0x56, 0x57,
        0x41, 0x56, 0x41, 0x57, 0x48, 0x8B, 0xEC, 0x48,
        0x83, 0xEC, 0x60
    ];

    private static readonly byte[] ReadStringWideCharPointerPrefix =
    [
        0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55,
        0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x00,
        0x01, 0x00, 0x00
    ];

    private static NativeInlineHook<ReadStringDelegate>? _acStringHook;
    private static NativeInlineHook<ReadStringDelegate>? _wideCharPointerHook;
    private static GetFilerCodePageIdDelegate? _getFilerCodePageId;
    private static CodePageIdIsDoubleByteDelegate? _codePageIdIsDoubleByte;
    private static IntPtr _moduleBase;
    private static bool _installed;
    private static int _acStringHitCount;
    private static int _wideCharPointerHitCount;
    private static int _invalidCodePageCount;
    private static int _dbcsScopeCount;
    private static int _externalScopeCount;
    private static int _scopeLogCount;
    private static int _stringLogCount;
    private static int _lastCodePageId;
    private static bool _lastCodePageIsDoubleByte;
    private static IntPtr _lastFiler;
    private static uint _lastAcStringReturnRva;
    private static uint _lastWideCharPointerReturnRva;
    private static uint _lastWideCharStringReturnRva;
    private static string _lastWideCharString = string.Empty;
    private static readonly object LoggedWideCharOutputLock = new();
    private static readonly HashSet<string> LoggedWideCharOutputs = new(StringComparer.Ordinal);
    [ThreadStatic] private static ScopeNode? _currentScope;

    /// <summary>
    /// readString 原生函数委托。
    /// </summary>
    /// <param name="filer">AcDbMemoryDwgFiler 指针。</param>
    /// <param name="output">输出字符串参数。</param>
    /// <returns>Acad::ErrorStatus。</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadStringDelegate(IntPtr filer, IntPtr output);

    /// <summary>
    /// <c>acdbGetFilerCodePageId</c> 委托。
    /// </summary>
    /// <param name="filer">AcDbDwgFiler 指针。</param>
    /// <returns>AutoCAD 内部 code_page_id。</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetFilerCodePageIdDelegate(IntPtr filer);

    /// <summary>
    /// <c>AcCodePage::CodePageIdIsDoubleByte</c> 委托。
    /// </summary>
    /// <param name="codePageId">AutoCAD 内部 code_page_id。</param>
    /// <returns>若为 DBCS code page 则返回 true。</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool CodePageIdIsDoubleByteDelegate(int codePageId);

    /// <summary>安装 readString 作用域 Hook。</summary>
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

        if (!TryBindNativeExports(_moduleBase))
            return;

        bool hasAcString = TryGetExportAddress(
            _moduleBase,
            ReadStringAcStringExport,
            "AcDbMemoryDwgFiler.readString(AcString&)",
            out IntPtr acStringAddress,
            out uint acStringRva);
        bool hasWideChar = TryGetExportAddress(
            _moduleBase,
            ReadStringWideCharPointerExport,
            "AcDbMemoryDwgFiler.readString(wchar_t**)",
            out IntPtr wideCharAddress,
            out uint wideCharRva);

        bool acStringInstalled = false;
        if (hasAcString)
        {
            _acStringHook = new NativeInlineHook<ReadStringDelegate>(
                Tag,
                "AcDbMemoryDwgFiler.readString(AcString&)",
                acStringRva);
            acStringInstalled = _acStringHook.InstallAtAddress(
                acStringAddress,
                acStringRva,
                AcStringReadStringHookHandler,
                ReadStringAcStringPrefix.Length,
                64,
                ReadStringAcStringPrefix);
        }

        bool wideCharInstalled = false;
        if (hasWideChar)
        {
            _wideCharPointerHook = new NativeInlineHook<ReadStringDelegate>(
                Tag,
                "AcDbMemoryDwgFiler.readString(wchar_t**)",
                wideCharRva);
            wideCharInstalled = _wideCharPointerHook.InstallAtAddress(
                wideCharAddress,
                wideCharRva,
                WideCharPointerReadStringHookHandler,
                ReadStringWideCharPointerPrefix.Length,
                64,
                ReadStringWideCharPointerPrefix);
        }

        _installed = acStringInstalled || wideCharInstalled;
        DiagnosticLogger.Log(Tag, $"安装完成。AcString={acStringInstalled}, WCharPtr={wideCharInstalled}");
    }

    /// <summary>卸载 readString 作用域 Hook。</summary>
    public static void Uninstall()
    {
        _acStringHook?.Uninstall();
        _wideCharPointerHook?.Uninstall();
        _acStringHook = null;
        _wideCharPointerHook = null;
        _getFilerCodePageId = null;
        _codePageIdIsDoubleByte = null;
        _moduleBase = IntPtr.Zero;
        _currentScope = null;
        lock (LoggedWideCharOutputLock)
        {
            LoggedWideCharOutputs.Clear();
        }

        _installed = false;
        DiagnosticLogger.Log(Tag, $"已卸载。AcStringHits={_acStringHitCount}, WCharPtrHits={_wideCharPointerHitCount}, DbcsScopes={_dbcsScopeCount}");
    }

    /// <summary>
    /// 尝试获取当前线程 readString 作用域中的 DBCS code page。
    /// </summary>
    /// <param name="codePageId">当前 DWG filer 的 code_page_id。</param>
    /// <returns>当前线程处于 readString 作用域且 code page 为 DBCS 时返回 true。</returns>
    public static bool TryGetCurrentDbcsCodePageId(out int codePageId)
    {
        var scope = _currentScope;
        if (scope != null && scope.IsDoubleByte)
        {
            codePageId = scope.CodePageId;
            return true;
        }

        codePageId = 0;
        return false;
    }

    /// <summary>获取诊断报告。</summary>
    public static string GetReport()
    {
        var scope = _currentScope;
        string depth = scope == null ? "0" : scope.Depth.ToString();
        return string.Join(Environment.NewLine,
            "=== DWG Filer Code Page Scope ===",
            $"Installed: {_installed}",
            $"CurrentScopeDepth: {depth}",
            $"AcStringReadStringHits: {_acStringHitCount}",
            $"WideCharPointerReadStringHits: {_wideCharPointerHitCount}",
            $"DbcsScopeCount: {_dbcsScopeCount}",
            $"ExternalScopeCount: {_externalScopeCount}",
            $"InvalidCodePageCount: {_invalidCodePageCount}",
            $"LastFiler: 0x{_lastFiler.ToInt64():X}",
            $"LastCodePageId: {FormatCodePageId(_lastCodePageId)}",
            $"LastIsDoubleByte: {_lastCodePageIsDoubleByte}",
            $"LastAcStringReturnRva: 0x{_lastAcStringReturnRva:X}",
            $"LastWideCharPointerReturnRva: 0x{_lastWideCharPointerReturnRva:X}",
            $"LastWideCharStringReturnRva: 0x{_lastWideCharStringReturnRva:X}",
            $"LastWideCharString: {_lastWideCharString}");
    }

    /// <summary>
    /// 在非 readString 的 DWG 反序列化窗口中压入当前 filer 的 code page 作用域。
    /// </summary>
    /// <param name="filer">AcDbDwgFiler 指针。</param>
    /// <param name="owner">外层 native 调用点名称。</param>
    /// <param name="returnRva">外层调用返回 RVA，仅用于诊断。</param>
    /// <returns>成功压入作用域时返回释放句柄；否则返回 null。</returns>
    public static IDisposable? EnterExternalScope(IntPtr filer, string owner, uint returnRva)
    {
        int codePageId = GetFilerCodePageIdSafe(filer);
        bool isDoubleByte = IsDoubleByteCodePageId(codePageId);
        if (!isDoubleByte)
        {
            if (codePageId == 0)
                Interlocked.Increment(ref _invalidCodePageCount);

            return null;
        }

        PushScope(filer, codePageId, isDoubleByte);
        Interlocked.Increment(ref _dbcsScopeCount);
        int externalScopeCount = Interlocked.Increment(ref _externalScopeCount);
        if (externalScopeCount <= ScopeLogLimit)
        {
            DiagnosticLogger.Log(Tag,
                $"{owner}: external return=0x{returnRva:X}, filer=0x{filer.ToInt64():X}, " +
                $"codePage={FormatCodePageId(codePageId)}, dbcs={isDoubleByte}");
        }

        return new ScopeCookie();
    }

    /// <summary>
    /// 格式化 AutoCAD 内部 code_page_id。
    /// </summary>
    /// <param name="codePageId">AutoCAD 内部 code_page_id。</param>
    /// <returns>用于诊断输出的字符串。</returns>
    public static string FormatCodePageId(int codePageId)
    {
        return codePageId switch
        {
            0x27 => "0x27(GBK/CP936 observed)",
            0x28 => "0x28(Big5/CP950 observed)",
            0 => "0x0(None)",
            _ => $"0x{codePageId:X}"
        };
    }

    private static bool IsSupportedPlatform()
    {
        if (!PlatformManager.Platform.AcDbDllName.Equals("acdb25.dll", StringComparison.OrdinalIgnoreCase)
            || !PlatformManager.Platform.VersionName.Equals("2025", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticLogger.Log(Tag,
                $"{PlatformManager.Platform.DisplayName} 未验证 readString/code-page 导出符号，跳过安装。");
            return false;
        }

        return true;
    }

    private static bool TryBindNativeExports(IntPtr module)
    {
        if (!TryGetExportAddress(
                module,
                GetFilerCodePageIdExport,
                "acdbGetFilerCodePageId",
                out IntPtr getFilerCodePageId,
                out _)
            || !TryGetExportAddress(
                module,
                CodePageIdIsDoubleByteExport,
                "AcCodePage::CodePageIdIsDoubleByte",
                out IntPtr codePageIdIsDoubleByte,
                out _))
            return false;

        _getFilerCodePageId = Marshal.GetDelegateForFunctionPointer<GetFilerCodePageIdDelegate>(getFilerCodePageId);
        _codePageIdIsDoubleByte = Marshal.GetDelegateForFunctionPointer<CodePageIdIsDoubleByteDelegate>(codePageIdIsDoubleByte);
        return true;
    }

    private static bool TryGetExportAddress(
        IntPtr module,
        string exportName,
        string displayName,
        out IntPtr address,
        out uint rva)
    {
        address = NativeInlineHookInterop.GetProcAddress(module, exportName);
        rva = 0;
        if (address == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{displayName} 导出符号未找到，跳过。");
            return false;
        }

        long delta = address.ToInt64() - module.ToInt64();
        if (delta <= 0 || delta > uint.MaxValue || !IsCommittedMemory(address))
        {
            DiagnosticLogger.Log(Tag, $"{displayName} 导出地址无效，跳过。Address=0x{address.ToInt64():X}");
            address = IntPtr.Zero;
            return false;
        }

        rva = (uint)delta;
        DiagnosticLogger.Log(Tag, $"{displayName} 导出解析成功。RVA=0x{rva:X}");
        return true;
    }

    private static int AcStringReadStringHookHandler(IntPtr filer, IntPtr output)
    {
        Interlocked.Increment(ref _acStringHitCount);
        return InvokeReadString(_acStringHook, filer, output, "AcString");
    }

    private static int WideCharPointerReadStringHookHandler(IntPtr filer, IntPtr output)
    {
        Interlocked.Increment(ref _wideCharPointerHitCount);
        return InvokeReadString(_wideCharPointerHook, filer, output, "WCharPtr");
    }

    private static int InvokeReadString(
        NativeInlineHook<ReadStringDelegate>? hook,
        IntPtr filer,
        IntPtr output,
        string overloadName)
    {
        var trampoline = hook?.TrampolineDelegate;
        if (trampoline == null)
            return 0;

        int codePageId = GetFilerCodePageIdSafe(filer);
        bool isDoubleByte = IsDoubleByteCodePageId(codePageId);
        uint returnRva = GetReturnRva(hook?.CapturedReturnAddress ?? IntPtr.Zero);
        if (overloadName.Equals("AcString", StringComparison.Ordinal))
            _lastAcStringReturnRva = returnRva;
        else
            _lastWideCharPointerReturnRva = returnRva;

        PushScope(filer, codePageId, isDoubleByte);

        if (isDoubleByte)
        {
            Interlocked.Increment(ref _dbcsScopeCount);
        }
        else if (codePageId == 0)
        {
            Interlocked.Increment(ref _invalidCodePageCount);
        }

        if (Interlocked.Increment(ref _scopeLogCount) <= ScopeLogLimit)
        {
            DiagnosticLogger.Log(Tag,
                $"{overloadName}: return=0x{returnRva:X}, filer=0x{filer.ToInt64():X}, " +
                $"codePage={FormatCodePageId(codePageId)}, dbcs={isDoubleByte}");
        }

        try
        {
            int status = trampoline(filer, output);
            if (status == 0 && isDoubleByte && overloadName.Equals("WCharPtr", StringComparison.Ordinal))
            {
                TryLogWideCharOutput(output, returnRva, codePageId);
            }
            return status;
        }
        finally
        {
            PopScope();
        }
    }

    private static int GetFilerCodePageIdSafe(IntPtr filer)
    {
        try
        {
            if (filer == IntPtr.Zero || !IsCommittedMemory(filer) || _getFilerCodePageId == null)
                return 0;

            int codePageId = _getFilerCodePageId(filer);
            _lastFiler = filer;
            _lastCodePageId = codePageId;
            _lastCodePageIsDoubleByte = IsDoubleByteCodePageId(codePageId);
            return codePageId;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": 读取 filer code page 失败", ex);
            return 0;
        }
    }

    private static bool IsDoubleByteCodePageId(int codePageId)
    {
        try
        {
            return codePageId != 0
                && _codePageIdIsDoubleByte != null
                && _codePageIdIsDoubleByte(codePageId);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": 判断 DBCS code page 失败", ex);
            return false;
        }
    }

    private static void TryLogWideCharOutput(IntPtr output, uint returnRva, int codePageId)
    {
        try
        {
            if (output == IntPtr.Zero || !IsCommittedMemory(output))
                return;

            IntPtr textPtr = Marshal.ReadIntPtr(output);
            if (textPtr == IntPtr.Zero || !IsCommittedMemory(textPtr))
                return;

            string text = ReadNullTerminatedWideString(textPtr, MaxLoggedStringChars);
            if (string.IsNullOrEmpty(text) || !ContainsNonAscii(text))
                return;

            _lastWideCharStringReturnRva = returnRva;
            _lastWideCharString = EscapeForLog(text);
            if (!TryRememberWideCharOutput(returnRva, codePageId, _lastWideCharString))
                return;

            if (Interlocked.Increment(ref _stringLogCount) <= StringLogLimit)
            {
                DiagnosticLogger.Log(Tag,
                    $"WCharPtr输出: return=0x{returnRva:X}, codePage={FormatCodePageId(codePageId)}, " +
                    $"len={text.Length}, text='{_lastWideCharString}'");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": 记录 WCharPtr 输出失败", ex);
        }
    }

    private static bool TryRememberWideCharOutput(uint returnRva, int codePageId, string text)
    {
        lock (LoggedWideCharOutputLock)
        {
            if (LoggedWideCharOutputs.Count >= StringLogLimit)
                return true;

            return LoggedWideCharOutputs.Add($"0x{returnRva:X}|{codePageId:X}|{text}");
        }
    }

    private static string ReadNullTerminatedWideString(IntPtr textPtr, int maxChars)
    {
        var chars = new char[maxChars];
        int length = 0;
        for (; length < chars.Length; length++)
        {
            short value = Marshal.ReadInt16(textPtr, length * sizeof(char));
            if (value == 0)
                break;

            chars[length] = (char)value;
        }

        return new string(chars, 0, length);
    }

    private static bool ContainsNonAscii(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
                return true;
        }

        return false;
    }

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
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

    private static void PushScope(IntPtr filer, int codePageId, bool isDoubleByte)
    {
        var previous = _currentScope;
        int depth = previous == null ? 1 : previous.Depth + 1;
        _currentScope = new ScopeNode(previous, filer, codePageId, isDoubleByte, depth);
    }

    private static void PopScope()
    {
        _currentScope = _currentScope?.Previous;
    }

    private sealed class ScopeCookie : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            PopScope();
        }
    }

    private static bool IsCommittedMemory(IntPtr address)
    {
        try
        {
            return VirtualQuery(address, out MemoryBasicInformation info, (uint)Marshal.SizeOf<MemoryBasicInformation>()) != IntPtr.Zero
                && info.State == MemCommit;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, uint dwLength);

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

    private sealed class ScopeNode
    {
        public ScopeNode(ScopeNode? previous, IntPtr filer, int codePageId, bool isDoubleByte, int depth)
        {
            Previous = previous;
            Filer = filer;
            CodePageId = codePageId;
            IsDoubleByte = isDoubleByte;
            Depth = depth;
        }

        public ScopeNode? Previous { get; }
        public IntPtr Filer { get; }
        public int CodePageId { get; }
        public bool IsDoubleByte { get; }
        public int Depth { get; }
    }
}
#endif
