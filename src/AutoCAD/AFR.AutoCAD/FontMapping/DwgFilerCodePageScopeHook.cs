using System.Runtime.InteropServices;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DWG 字符串读取 code page 作用域 Hook。
/// <para>
/// 通过 hook <c>AcDbMemoryDwgFiler::readString</c> 的两个重载，在 AutoCAD 原生解码字符串期间
/// 从 DWG filer 读取真实 <c>code_page_id</c>，供 DBText native evidence 链路建立强信号。
/// </para>
/// </summary>
internal static class DwgFilerCodePageScopeHook
{
    private const string Tag = "DwgCodePageScope";
    private const uint MemCommit = 0x1000;
    private const int ScopeLogLimit = 16;
    private const int StringLogLimit = 48;
    private const int DbTextReadStringLogLimit = 40;
    private const int MaxLoggedStringChars = 160;
    private const int MaxReadStringRawBytes = 96;

    private static NativeInlineHook<ReadStringDelegate>? _acStringHook;
    private static NativeInlineHook<ReadStringDelegate>? _wideCharPointerHook;
    private static GetFilerCodePageIdDelegate? _getFilerCodePageId;
    private static CodePageIdIsDoubleByteDelegate? _codePageIdIsDoubleByte;
    private static DwgFilerCodePageHookProfile? _profile;
    private static IntPtr _moduleBase;
    private static bool _installed;
    private static int _acStringHitCount;
    private static int _wideCharPointerHitCount;
    private static int _invalidCodePageCount;
    private static int _dbcsScopeCount;
    private static int _externalScopeCount;
    private static int _scopeLogCount;
    private static int _stringLogCount;
    private static int _dbTextReadStringTraceCount;
    private static int _dbTextReadStringAttemptCount;
    private static int _dbTextReadStringStatusNonZeroCount;
    private static int _dbTextReadStringEmptyOutputCount;
    private static int _dbTextReadStringRecordMissCount;
    private static int _dbTextReadStringTraceLogCount;
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetFilerCodePageRecordDelegate(IntPtr filer);

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

        if (!NativeDecodeHookProfileResolver.TryGetCurrent(Tag, out var nativeProfile))
            return;

        var profile = nativeProfile.DwgFilerCodePage;
        _profile = profile;
        if (!profile.HasInstallableReadStringHook)
        {
            DiagnosticLogger.Log(Tag, $"{nativeProfile.PlatformName}: {profile.ReadStringAcString.DisabledReason ?? profile.ReadStringWideCharPointer.DisabledReason ?? nativeProfile.SupportNote}");
            return;
        }

        _moduleBase = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (_moduleBase == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过安装。");
            return;
        }

        if (!TryBindNativeExports(_moduleBase, profile))
            return;

        bool hasAcString = TryGetExportAddress(
            _moduleBase,
            profile.ReadStringAcString,
            out IntPtr acStringAddress,
            out uint acStringRva);
        bool hasWideChar = TryGetExportAddress(
            _moduleBase,
            profile.ReadStringWideCharPointer,
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
                profile.ReadStringAcString.MinPrologueSize,
                profile.ReadStringAcString.MaxPrologueSize,
                profile.ReadStringAcString.ExpectedPrefix);
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
                profile.ReadStringWideCharPointer.MinPrologueSize,
                profile.ReadStringWideCharPointer.MaxPrologueSize,
                profile.ReadStringWideCharPointer.ExpectedPrefix);
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
        _profile = null;
        _moduleBase = IntPtr.Zero;
        _currentScope = null;
        lock (LoggedWideCharOutputLock)
        {
            LoggedWideCharOutputs.Clear();
        }

        _installed = false;
        DiagnosticLogger.Log(Tag,
            $"已卸载。AcStringHits={_acStringHitCount}, WCharPtrHits={_wideCharPointerHitCount}, " +
            $"DbcsScopes={_dbcsScopeCount}, DbTextReadStringAttempts={_dbTextReadStringAttemptCount}, " +
            $"DbTextReadStringTraces={_dbTextReadStringTraceCount}, " +
            $"DbTextReadStringStatusNonZero={_dbTextReadStringStatusNonZeroCount}, " +
            $"DbTextReadStringEmptyOutput={_dbTextReadStringEmptyOutputCount}, " +
            $"DbTextReadStringRecordMiss={_dbTextReadStringRecordMissCount}");
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
            $"DbTextReadStringAttemptCount: {_dbTextReadStringAttemptCount}",
            $"DbTextReadStringTraceCount: {_dbTextReadStringTraceCount}",
            $"DbTextReadStringStatusNonZeroCount: {_dbTextReadStringStatusNonZeroCount}",
            $"DbTextReadStringEmptyOutputCount: {_dbTextReadStringEmptyOutputCount}",
            $"DbTextReadStringRecordMissCount: {_dbTextReadStringRecordMissCount}",
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

    private static bool TryBindNativeExports(IntPtr module, DwgFilerCodePageHookProfile profile)
    {
        if (!TryGetExportAddress(
                module,
                profile.CodePageIdIsDoubleByte,
                out IntPtr codePageIdIsDoubleByte,
                out _))
            return false;

        _codePageIdIsDoubleByte = Marshal.GetDelegateForFunctionPointer<CodePageIdIsDoubleByteDelegate>(codePageIdIsDoubleByte);

        if (profile.ResolverKind == FilerCodePageResolverKind.Export)
        {
            if (!TryGetExportAddress(
                    module,
                    profile.GetFilerCodePageId,
                    out IntPtr getFilerCodePageId,
                    out _))
                return false;

            _getFilerCodePageId = Marshal.GetDelegateForFunctionPointer<GetFilerCodePageIdDelegate>(getFilerCodePageId);
            return true;
        }

        if (profile.ResolverKind == FilerCodePageResolverKind.VirtualRecord
            && profile.VirtualCodePageMethodOffset > 0
            && profile.VirtualCodePageRecordCodePageOffset >= 0)
            return true;

        DiagnosticLogger.Log(Tag, "当前 profile 未提供可验证 code-page resolver，跳过 readString 作用域 hook。");
        return false;
    }

    private static bool TryGetExportAddress(
        IntPtr module,
        NativeHookTarget target,
        out IntPtr address,
        out uint rva)
    {
        if (!target.IsEnabled)
        {
            address = IntPtr.Zero;
            rva = 0;
            DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：{target.DisabledReason ?? "缺少导出符号或 RVA"}");
            return false;
        }

        string? exportName = target.ExportName;
        if (string.IsNullOrWhiteSpace(exportName))
        {
            if (!target.Rva.HasValue || target.Rva.Value > int.MaxValue)
            {
                address = IntPtr.Zero;
                rva = 0;
                DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：缺少导出符号或有效 RVA。");
                return false;
            }

            rva = target.Rva.Value;
            address = module + (int)rva;
            if (!IsCommittedMemory(address))
            {
                DiagnosticLogger.Log(Tag, $"{target.Name} RVA 地址无效，跳过。RVA=0x{rva:X}, Address=0x{address.ToInt64():X}");
                address = IntPtr.Zero;
                rva = 0;
                return false;
            }

            DiagnosticLogger.Log(Tag, $"{target.Name} RVA 解析成功。RVA=0x{rva:X}");
            return true;
        }

        address = NativeInlineHookInterop.GetProcAddress(module, exportName!);
        rva = 0;
        if (address == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 导出符号未找到，跳过。");
            return false;
        }

        long delta = address.ToInt64() - module.ToInt64();
        if (delta <= 0 || delta > uint.MaxValue || !IsCommittedMemory(address))
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 导出地址无效，跳过。Address=0x{address.ToInt64():X}");
            address = IntPtr.Zero;
            return false;
        }

        rva = (uint)delta;
        if (target.Rva.HasValue && target.Rva.Value != rva)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} RVA 不匹配，跳过。Expected=0x{target.Rva.Value:X}, Actual=0x{rva:X}");
            address = IntPtr.Zero;
            rva = 0;
            return false;
        }

        DiagnosticLogger.Log(Tag, $"{target.Name} 导出解析成功。RVA=0x{rva:X}");
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

        TryCaptureFilerPosition(filer, out FilerPosition beforePosition);

        bool inDbTextScope = DbTextDwgInFieldsScopeHook.TryGetCurrentDbTextScope(out _, out _);
        try
        {
            int status = trampoline(filer, output);
            if (inDbTextScope)
            {
                TryCaptureFilerPosition(filer, out FilerPosition afterPosition);
                byte[] rawBytes = TryReadFilerRawBytes(beforePosition, afterPosition, MaxReadStringRawBytes);
                string text = isDoubleByte
                    ? overloadName.Equals("AcString", StringComparison.Ordinal)
                        ? ReadAcString(output, MaxLoggedStringChars)
                        : ReadWideCharPointerOutput(output, MaxLoggedStringChars)
                    : string.Empty;
                TryRecordDbTextReadStringTrace(
                    overloadName,
                    codePageId,
                    isDoubleByte,
                    status,
                    returnRva,
                    beforePosition,
                    afterPosition,
                    rawBytes,
                    text);

                if (status == 0 && isDoubleByte && overloadName.Equals("WCharPtr", StringComparison.Ordinal))
                    TryLogWideCharOutput(text, returnRva, codePageId);
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
            if (filer == IntPtr.Zero || !IsCommittedMemory(filer))
                return 0;

            int codePageId;
            if (_getFilerCodePageId != null)
            {
                codePageId = _getFilerCodePageId(filer);
            }
            else if (!TryGetFilerCodePageIdViaVirtualRecord(filer, out codePageId))
            {
                return 0;
            }

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

    private static bool TryGetFilerCodePageIdViaVirtualRecord(IntPtr filer, out int codePageId)
    {
        codePageId = 0;
        var profile = _profile;
        if (profile == null
            || profile.ResolverKind != FilerCodePageResolverKind.VirtualRecord
            || profile.VirtualCodePageMethodOffset <= 0)
            return false;

        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(filer);
            if (vtable == IntPtr.Zero || !IsCommittedMemory(vtable + profile.VirtualCodePageMethodOffset))
                return false;

            IntPtr method = Marshal.ReadIntPtr(vtable + profile.VirtualCodePageMethodOffset);
            if (method == IntPtr.Zero || !IsCommittedMemory(method))
                return false;

            var resolver = Marshal.GetDelegateForFunctionPointer<GetFilerCodePageRecordDelegate>(method);
            IntPtr record = resolver(filer);
            IntPtr codePageAddress = record + profile.VirtualCodePageRecordCodePageOffset;
            if (record == IntPtr.Zero || !IsCommittedMemory(codePageAddress))
                return false;

            codePageId = Marshal.ReadInt32(codePageAddress);
            return codePageId != 0;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": 虚表 resolver 读取 filer code page 失败", ex);
            codePageId = 0;
            return false;
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

    private static string ReadWideCharPointerOutput(IntPtr output, int maxChars)
    {
        try
        {
            if (output == IntPtr.Zero || !IsCommittedMemory(output))
                return string.Empty;

            IntPtr textPtr = Marshal.ReadIntPtr(output);
            if (textPtr == IntPtr.Zero || !IsCommittedMemory(textPtr))
                return string.Empty;

            return ReadNullTerminatedWideString(textPtr, maxChars);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadAcString(IntPtr acString, int maxChars)
    {
        try
        {
            if (acString == IntPtr.Zero
                || !IsCommittedMemory(acString)
                || !IsCommittedMemory(acString + 0x18))
                return string.Empty;

            int lengthMarker = Marshal.ReadInt32(acString, 4);
            int length = lengthMarker < 0
                ? lengthMarker & 0x3FFFFFFF
                : lengthMarker;
            if (length <= 0 || length > maxChars)
                return string.Empty;

            IntPtr textPtr = lengthMarker < 0
                ? Marshal.ReadIntPtr(acString, 8)
                : acString + 8;
            if (textPtr == IntPtr.Zero || !IsCommittedMemory(textPtr))
                return string.Empty;

            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                IntPtr current = textPtr + (i * sizeof(char));
                if (!IsCommittedMemory(current))
                    return string.Empty;

                chars[i] = (char)Marshal.ReadInt16(current);
            }

            return new string(chars);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryLogWideCharOutput(string text, uint returnRva, int codePageId)
    {
        try
        {
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

    private static void TryRecordDbTextReadStringTrace(
        string overloadName,
        int codePageId,
        bool isDoubleByte,
        int status,
        uint returnRva,
        FilerPosition beforePosition,
        FilerPosition afterPosition,
        byte[] rawBytes,
        string text)
    {
        Interlocked.Increment(ref _dbTextReadStringAttemptCount);
        if (status != 0)
            Interlocked.Increment(ref _dbTextReadStringStatusNonZeroCount);
        if (string.IsNullOrEmpty(text) && rawBytes.Length == 0)
            Interlocked.Increment(ref _dbTextReadStringEmptyOutputCount);

        var readStringEvent = new NativeReadStringEvent(
            overloadName,
            codePageId,
            isDoubleByte,
            status,
            returnRva,
            beforePosition.ByteOffset,
            beforePosition.BitOffset,
            afterPosition.ByteOffset,
            afterPosition.BitOffset,
            rawBytes,
            text);
        if (!DbTextDwgInFieldsScopeHook.TryRecordReadStringEvent(readStringEvent))
        {
            Interlocked.Increment(ref _dbTextReadStringRecordMissCount);
            return;
        }

        Interlocked.Increment(ref _dbTextReadStringTraceCount);
        if (Interlocked.Increment(ref _dbTextReadStringTraceLogCount) > DbTextReadStringLogLimit)
            return;

        DiagnosticLogger.Log(Tag,
            $"DBText readString: {overloadName}, return=0x{returnRva:X}, status={status}, " +
            $"codePage={FormatCodePageId(codePageId)}, dbcs={isDoubleByte}, " +
            $"pos={FormatPosition(beforePosition)}->{FormatPosition(afterPosition)}, " +
            $"raw={FormatBytes(rawBytes, 32)}, text='{EscapeForLog(text)}'");
    }

    private static bool TryCaptureFilerPosition(IntPtr filer, out FilerPosition position)
    {
        position = default;
        var profile = _profile;
        if (profile == null)
            return false;

        try
        {
            if (filer == IntPtr.Zero
                || !IsCommittedMemory(filer + profile.FilerBitOffsetOffset)
                || !IsCommittedMemory(filer + profile.FilerBufferPointerOffset)
                || !IsCommittedMemory(filer + profile.FilerByteOffsetOffset))
                return false;

            IntPtr buffer = Marshal.ReadIntPtr(filer + profile.FilerBufferPointerOffset);
            int byteOffset = Marshal.ReadInt32(filer + profile.FilerByteOffsetOffset);
            int bitOffset = Marshal.ReadInt32(filer + profile.FilerBitOffsetOffset);
            if (buffer == IntPtr.Zero || byteOffset < 0 || bitOffset < 0 || bitOffset > 7)
                return false;

            position = new FilerPosition(buffer, byteOffset, bitOffset);
            return true;
        }
        catch
        {
            position = default;
            return false;
        }
    }

    private static byte[] TryReadFilerRawBytes(FilerPosition before, FilerPosition after, int limit)
    {
        if (before.Buffer == IntPtr.Zero
            || after.Buffer == IntPtr.Zero
            || before.Buffer != after.Buffer
            || before.ByteOffset < 0
            || after.ByteOffset < before.ByteOffset)
            return [];

        int endExclusive = after.ByteOffset + (after.BitOffset > 0 ? 1 : 0);
        int length = endExclusive - before.ByteOffset;
        if (length <= 0)
            return [];

        length = Math.Min(length, limit);
        var bytes = new byte[length];
        int count = 0;
        for (; count < length; count++)
        {
            IntPtr current = before.Buffer + before.ByteOffset + count;
            if (!IsCommittedMemory(current))
                break;

            bytes[count] = Marshal.ReadByte(current);
        }

        if (count == bytes.Length)
            return bytes;

        Array.Resize(ref bytes, count);
        return bytes;
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

    private static string FormatPosition(FilerPosition position)
    {
        return position.Buffer == IntPtr.Zero
            ? "<none>"
            : $"{position.ByteOffset}:{position.BitOffset}";
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

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("'", "\\'");
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

    private readonly record struct FilerPosition(IntPtr Buffer, int ByteOffset, int BitOffset);
}
