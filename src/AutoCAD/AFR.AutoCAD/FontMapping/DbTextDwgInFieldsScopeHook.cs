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
    private const uint WideStringAssignRva = 0x4EF44;
    private const int ImpTextStringConstVtableOffset = 0x560;
    private const int ImpTextStringPointerOffset = 0x68;
    private const uint MemCommit = 0x1000;
    private const int ProvenanceLogLimit = 80;
    private const int TextSetEventLogLimit = 80;
    private const int MaxNativeWideStringChars = 512;
    private const int MaxDwgInRawBytes = 2048;
    private const int FilerBitOffsetOffset = 0x10;
    private const int FilerBufferPointerOffset = 0x30;
    private const int FilerByteOffsetOffset = 0x50;
    private const int FilerMethodStatusOffset = 0x40;
    private const int FilerMethodTextAuxOffset = 0x88;
    private const int FilerMethodTextReadOffset = 0xB8;
    private const int FilerMethodReadStringAcStringOffset = 0xC0;
    private const int FilerMethodReadStringWideOffset = 0xC8;
    private const int FilerMethodEarly188Offset = 0x188;
    private const int FilerMethodEarly280Offset = 0x280;
    private const int FilerMethodPostTextOffset = 0x290;

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

    private static readonly byte[] WideStringAssignPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x08,
        0x48, 0x89, 0x74, 0x24, 0x10,
        0x57,
        0x48, 0x83, 0xEC, 0x20
    ];

    private static NativeInlineHook<DwgInFieldsDelegate>? _hook;
    private static NativeInlineHook<WideStringAssignDelegate>? _wideStringAssignHook;
    private static IntPtr _moduleBase;
    private static bool _installed;
    private static int _hitCount;
    private static int _scopedHitCount;
    private static int _noScopeHitCount;
    private static int _errorCount;
    private static int _provenanceCount;
    private static int _nativeDbcsDecodedCharCount;
    private static int _readStringEventCount;
    private static int _readStringEventWithRawBytesCount;
    private static int _textSetSourceEventCount;
    private static int _textSetSourceEventLogCount;
    private static int _textSetSourceWrongDestinationCount;
    private static int _dwgInRawSnapshotCount;
    private static int _dwgInRawSnapshotTruncatedCount;
    private static int _provenanceWithoutDbcsSequenceCount;
    private static int _provenanceLogCount;
    private static uint _lastReturnRva;
    private static readonly object ProvenanceLock = new();
    private static readonly Dictionary<nint, NativeDbTextProvenance> ProvenanceByImpText = new();
    [ThreadStatic] private static bool _inHook;
    [ThreadStatic] private static bool _inWideStringAssignHook;
    [ThreadStatic] private static IntPtr _currentImpText;
    [ThreadStatic] private static List<NativeDbcsDecodeUnit>? _currentDecodedDbcsUnits;
    [ThreadStatic] private static List<NativeReadStringEvent>? _currentReadStringEvents;
    [ThreadStatic] private static List<NativeTextSetSourceEvent>? _currentTextSetSourceEvents;

    /// <summary>
    /// <c>AcDbImpText::dwgInFields(AcDbDwgFiler*)</c> 原生委托。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DwgInFieldsDelegate(IntPtr impText, IntPtr filer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WideStringAssignDelegate(IntPtr source, IntPtr destinationPointer);

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
        bool dwgInInstalled = _hook.Install(
            _moduleBase,
            HookHandler,
            AcDbImpTextDwgInFieldsPrefix.Length,
            64,
            AcDbImpTextDwgInFieldsPrefix);

        _wideStringAssignHook = new NativeInlineHook<WideStringAssignDelegate>(
            Tag,
            "DBText wide string assign",
            WideStringAssignRva);
        bool assignInstalled = _wideStringAssignHook.Install(
            _moduleBase,
            WideStringAssignHookHandler,
            WideStringAssignPrefix.Length,
            32,
            WideStringAssignPrefix);

        _installed = dwgInInstalled || assignInstalled;
        DiagnosticLogger.Log(Tag,
            $"安装完成。DwgInFields={dwgInInstalled}, WideStringAssignProbe={assignInstalled}");
    }

    /// <summary>卸载 DBText DWG 读字段作用域 Hook。</summary>
    public static void Uninstall()
    {
        _wideStringAssignHook?.Uninstall();
        _hook?.Uninstall();
        _wideStringAssignHook = null;
        _hook = null;
        _moduleBase = IntPtr.Zero;
        lock (ProvenanceLock)
        {
            ProvenanceByImpText.Clear();
        }

        _installed = false;
        DiagnosticLogger.Log(Tag,
            $"已卸载。HitCount={_hitCount}, ScopedHitCount={_scopedHitCount}, " +
            $"NoScopeHitCount={_noScopeHitCount}, ProvenanceCount={_provenanceCount}, " +
            $"TextSetSourceEvents={_textSetSourceEventCount}, ErrorCount={_errorCount}");
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
            $"ReadStringEventCount: {_readStringEventCount}",
            $"ReadStringEventWithRawBytesCount: {_readStringEventWithRawBytesCount}",
            $"TextSetSourceEventCount: {_textSetSourceEventCount}",
            $"TextSetSourceWrongDestinationCount: {_textSetSourceWrongDestinationCount}",
            $"DwgInRawSnapshotCount: {_dwgInRawSnapshotCount}",
            $"DwgInRawSnapshotTruncatedCount: {_dwgInRawSnapshotTruncatedCount}",
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
    /// 只读获取当前 <c>AcDbImpText</c> native getter 暴露的实时文字。
    /// </summary>
    public static bool TryReadCurrentNativeText(IntPtr impText, out string nativeText)
    {
        nativeText = string.Empty;
        if (impText == IntPtr.Zero)
            return false;

        try
        {
            nativeText = ReadImpTextString(impText);
            return !string.IsNullOrEmpty(nativeText);
        }
        catch
        {
            nativeText = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// 尝试读取当前线程正在执行的 DBText DWG 读入作用域。
    /// </summary>
    public static bool TryGetCurrentDbTextScope(out IntPtr impText, out int codePageId)
    {
        impText = _currentImpText;
        if (impText == IntPtr.Zero)
        {
            codePageId = 0;
            return false;
        }

        if (DwgFilerCodePageScopeHook.TryGetCurrentDbcsCodePageId(out codePageId))
            return true;

        codePageId = 0;
        return false;
    }

    /// <summary>
    /// 记录当前 DBText 读入作用域内 native DBCS 解码得到的字符。
    /// </summary>
    public static void RecordNativeDecodedDbcsChar(byte firstByte, byte secondByte, char value)
    {
        if (value == '\0' || _currentImpText == IntPtr.Zero)
            return;

        _currentDecodedDbcsUnits?.Add(new NativeDbcsDecodeUnit(firstByte, secondByte, value));
        Interlocked.Increment(ref _nativeDbcsDecodedCharCount);
    }

    /// <summary>
    /// 记录当前 DBText 读入作用域内的完整 readString 事件。
    /// </summary>
    public static bool TryRecordReadStringEvent(NativeReadStringEvent readStringEvent)
    {
        if (_currentImpText == IntPtr.Zero || _currentReadStringEvents == null)
            return false;

        _currentReadStringEvents.Add(readStringEvent);
        Interlocked.Increment(ref _readStringEventCount);
        if (readStringEvent.RawBytes.Length > 0)
            Interlocked.Increment(ref _readStringEventWithRawBytesCount);

        return true;
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
        List<NativeDbcsDecodeUnit>? previousDecodedDbcsUnits = _currentDecodedDbcsUnits;
        List<NativeReadStringEvent>? previousReadStringEvents = _currentReadStringEvents;
        List<NativeTextSetSourceEvent>? previousTextSetSourceEvents = _currentTextSetSourceEvents;
        _currentImpText = impText;
        _currentDecodedDbcsUnits = new List<NativeDbcsDecodeUnit>();
        _currentReadStringEvents = new List<NativeReadStringEvent>();
        _currentTextSetSourceEvents = new List<NativeTextSetSourceEvent>();
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

            TryCaptureFilerPosition(filer, out FilerPosition beforePosition);
            int status = trampoline(impText, filer);
            if (status == 0 && scope != null)
            {
                TryCaptureFilerPosition(filer, out FilerPosition afterPosition);
                NativeDwgInRawSnapshot rawSnapshot = CreateDwgInRawSnapshot(beforePosition, afterPosition);
                TryRememberProvenance(
                    impText,
                    filer,
                    _currentDecodedDbcsUnits,
                    _currentReadStringEvents,
                    _currentTextSetSourceEvents,
                    rawSnapshot);
            }

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
            _currentDecodedDbcsUnits = previousDecodedDbcsUnits;
            _currentReadStringEvents = previousReadStringEvents;
            _currentTextSetSourceEvents = previousTextSetSourceEvents;
            _inHook = false;
        }
    }

    private static int WideStringAssignHookHandler(IntPtr source, IntPtr destinationPointer)
    {
        var trampoline = _wideStringAssignHook?.TrampolineDelegate;
        if (trampoline == null)
            return 0;

        if (_inWideStringAssignHook)
            return trampoline(source, destinationPointer);

        _inWideStringAssignHook = true;
        try
        {
            TryRecordTextSetSource(source, destinationPointer);
            return trampoline(source, destinationPointer);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            DiagnosticLogger.LogError(Tag + ": WideStringAssignHookHandler 异常", ex);
            return trampoline(source, destinationPointer);
        }
        finally
        {
            _inWideStringAssignHook = false;
        }
    }

    private static void TryRememberProvenance(
        IntPtr impText,
        IntPtr filer,
        List<NativeDbcsDecodeUnit>? nativeDbcsUnits,
        List<NativeReadStringEvent>? readStringEvents,
        List<NativeTextSetSourceEvent>? textSetSourceEvents,
        NativeDwgInRawSnapshot rawSnapshot)
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

            string nativeDbcsDecodedText = BuildNativeDbcsDecodedText(nativeDbcsUnits);
            byte[] nativeDbcsBytes = BuildNativeDbcsBytes(nativeDbcsUnits);
            NativeReadStringEvent[] readStringEventSnapshot = readStringEvents?.ToArray() ?? [];
            NativeTextSetSourceEvent[] textSetSourceEventSnapshot = textSetSourceEvents?.ToArray() ?? [];
            NativeFilerVirtualSnapshot filerVirtualSnapshot = CaptureFilerVirtualSnapshot(filer);
            var provenance = new NativeDbTextProvenance(
                impText,
                filer,
                codePageId,
                nativeText,
                nativeDbcsDecodedText,
                nativeDbcsBytes,
                readStringEventSnapshot,
                textSetSourceEventSnapshot,
                filerVirtualSnapshot,
                rawSnapshot);
            if (nativeDbcsDecodedText.Length == 0)
                Interlocked.Increment(ref _provenanceWithoutDbcsSequenceCount);
            if (rawSnapshot.RawBytes.Length > 0)
            {
                Interlocked.Increment(ref _dwgInRawSnapshotCount);
                if (rawSnapshot.IsTruncated)
                    Interlocked.Increment(ref _dwgInRawSnapshotTruncatedCount);
            }

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
                    $"readStrings={readStringEventSnapshot.Length}, " +
                    $"textSetSources={textSetSourceEventSnapshot.Length}, " +
                    $"dwgRaw={FormatPosition(rawSnapshot.StartByteOffset, rawSnapshot.StartBitOffset)}->" +
                    $"{FormatPosition(rawSnapshot.EndByteOffset, rawSnapshot.EndBitOffset)} " +
                    $"len={rawSnapshot.RawBytes.Length}{(rawSnapshot.IsTruncated ? "+" : "")}, " +
                    $"vtable=0x{filerVirtualSnapshot.VTableRva:X}, " +
                    $"mB8=0x{filerVirtualSnapshot.MethodB8Rva:X}, " +
                    $"mC0=0x{filerVirtualSnapshot.MethodC0Rva:X}, " +
                    $"mC8=0x{filerVirtualSnapshot.MethodC8Rva:X}, " +
                    $"dbcsBytes={FormatBytes(nativeDbcsBytes, 24)}, " +
                    $"text='{EscapeForLog(nativeText)}'");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            DiagnosticLogger.LogError(Tag + ": 记录对象级证据失败", ex);
        }
    }

    private static void TryRecordTextSetSource(IntPtr source, IntPtr destinationPointer)
    {
        if (_currentImpText == IntPtr.Zero || _currentTextSetSourceEvents == null)
            return;

        long expectedDestination = _currentImpText.ToInt64() + ImpTextStringPointerOffset;
        if (destinationPointer.ToInt64() != expectedDestination)
        {
            Interlocked.Increment(ref _textSetSourceWrongDestinationCount);
            return;
        }

        string sourceText = ReadWideString(source, MaxNativeWideStringChars, out bool truncated);
        if (string.IsNullOrEmpty(sourceText))
            return;

        var item = new NativeTextSetSourceEvent(
            GetReturnRva(_wideStringAssignHook?.CapturedReturnAddress ?? IntPtr.Zero),
            source,
            destinationPointer,
            ImpTextStringPointerOffset,
            sourceText,
            truncated);
        _currentTextSetSourceEvents.Add(item);
        Interlocked.Increment(ref _textSetSourceEventCount);

        if (Interlocked.Increment(ref _textSetSourceEventLogCount) <= TextSetEventLogLimit)
        {
            DiagnosticLogger.Log(Tag,
                $"DBText text set source: impText=0x{_currentImpText.ToInt64():X}, " +
                $"return=0x{item.ReturnRva:X}, src=0x{source.ToInt64():X}, dst=+0x{ImpTextStringPointerOffset:X}, " +
                $"len={sourceText.Length}{(truncated ? "+" : "")}, text='{EscapeForLog(sourceText)}'");
        }
    }

    private static NativeFilerVirtualSnapshot CaptureFilerVirtualSnapshot(IntPtr filer)
    {
        try
        {
            if (filer == IntPtr.Zero || !IsCommittedMemory(filer))
                return NativeFilerVirtualSnapshot.Empty;

            IntPtr vtable = Marshal.ReadIntPtr(filer);
            if (vtable == IntPtr.Zero || !IsCommittedMemory(vtable))
                return NativeFilerVirtualSnapshot.Empty;

            return new NativeFilerVirtualSnapshot(
                GetRva(vtable),
                ReadMethodRva(vtable, FilerMethodStatusOffset),
                ReadMethodRva(vtable, FilerMethodTextAuxOffset),
                ReadMethodRva(vtable, FilerMethodTextReadOffset),
                ReadMethodRva(vtable, FilerMethodReadStringAcStringOffset),
                ReadMethodRva(vtable, FilerMethodReadStringWideOffset),
                ReadMethodRva(vtable, FilerMethodEarly188Offset),
                ReadMethodRva(vtable, FilerMethodEarly280Offset),
                ReadMethodRva(vtable, FilerMethodPostTextOffset));
        }
        catch
        {
            return NativeFilerVirtualSnapshot.Empty;
        }
    }

    private static uint ReadMethodRva(IntPtr vtable, int offset)
    {
        IntPtr slot = vtable + offset;
        if (!IsCommittedMemory(slot))
            return 0;

        IntPtr target = Marshal.ReadIntPtr(slot);
        return GetRva(target);
    }

    private static NativeDwgInRawSnapshot CreateDwgInRawSnapshot(FilerPosition before, FilerPosition after)
    {
        if (before.Buffer == IntPtr.Zero
            || after.Buffer == IntPtr.Zero
            || before.Buffer != after.Buffer
            || before.ByteOffset < 0
            || after.ByteOffset < before.ByteOffset)
        {
            return NativeDwgInRawSnapshot.Empty;
        }

        int endExclusive = after.ByteOffset + (after.BitOffset > 0 ? 1 : 0);
        int rawLength = endExclusive - before.ByteOffset;
        if (rawLength <= 0)
        {
            return new NativeDwgInRawSnapshot(
                before.ByteOffset,
                before.BitOffset,
                after.ByteOffset,
                after.BitOffset,
                [],
                false);
        }

        bool isTruncated = rawLength > MaxDwgInRawBytes;
        int capturedLength = Math.Min(rawLength, MaxDwgInRawBytes);
        var bytes = new byte[capturedLength];
        int count = 0;
        for (; count < capturedLength; count++)
        {
            IntPtr current = before.Buffer + before.ByteOffset + count;
            if (!IsCommittedMemory(current))
                break;

            bytes[count] = Marshal.ReadByte(current);
        }

        if (count != bytes.Length)
        {
            Array.Resize(ref bytes, count);
            isTruncated = true;
        }

        return new NativeDwgInRawSnapshot(
            before.ByteOffset,
            before.BitOffset,
            after.ByteOffset,
            after.BitOffset,
            bytes,
            isTruncated);
    }

    private static bool TryCaptureFilerPosition(IntPtr filer, out FilerPosition position)
    {
        position = default;
        try
        {
            if (filer == IntPtr.Zero
                || !IsCommittedMemory(filer + FilerBitOffsetOffset)
                || !IsCommittedMemory(filer + FilerBufferPointerOffset)
                || !IsCommittedMemory(filer + FilerByteOffsetOffset))
                return false;

            IntPtr buffer = Marshal.ReadIntPtr(filer + FilerBufferPointerOffset);
            int byteOffset = Marshal.ReadInt32(filer + FilerByteOffsetOffset);
            int bitOffset = Marshal.ReadInt32(filer + FilerBitOffsetOffset);
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

    private static string BuildNativeDbcsDecodedText(List<NativeDbcsDecodeUnit>? units)
    {
        if (units == null || units.Count == 0)
            return string.Empty;

        var chars = new char[units.Count];
        for (int i = 0; i < units.Count; i++)
            chars[i] = units[i].Value;

        return new string(chars);
    }

    private static byte[] BuildNativeDbcsBytes(List<NativeDbcsDecodeUnit>? units)
    {
        if (units == null || units.Count == 0)
            return [];

        var bytes = new byte[units.Count * 2];
        for (int i = 0; i < units.Count; i++)
        {
            bytes[i * 2] = units[i].FirstByte;
            bytes[(i * 2) + 1] = units[i].SecondByte;
        }

        return bytes;
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

    private static string ReadWideString(IntPtr source, int maxChars, out bool truncated)
    {
        truncated = false;
        if (source == IntPtr.Zero || maxChars <= 0 || !IsCommittedMemory(source))
            return string.Empty;

        try
        {
            var chars = new List<char>(Math.Min(maxChars, 128));
            for (int i = 0; i < maxChars; i++)
            {
                IntPtr current = source + (i * 2);
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

    private static uint GetReturnRva(IntPtr returnAddress)
    {
        return GetRva(returnAddress);
    }

    private static uint GetRva(IntPtr address)
    {
        if (address == IntPtr.Zero || _moduleBase == IntPtr.Zero)
            return 0;

        long rva = address.ToInt64() - _moduleBase.ToInt64();
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

    private static string FormatPosition(int byteOffset, int bitOffset)
    {
        return byteOffset < 0 || bitOffset < 0
            ? "<none>"
            : $"{byteOffset}:{bitOffset}";
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

    private readonly record struct FilerPosition(IntPtr Buffer, int ByteOffset, int BitOffset);
}

/// <summary>
/// native DBText 读入阶段记录的对象级证据。
/// </summary>
internal readonly record struct NativeDbTextProvenance(
    IntPtr ImpText,
    IntPtr Filer,
    int CodePageId,
    string NativeText,
    string NativeDbcsDecodedText,
    byte[] NativeDbcsBytes,
    NativeReadStringEvent[] ReadStringEvents,
    NativeTextSetSourceEvent[] TextSetSourceEvents,
    NativeFilerVirtualSnapshot FilerVirtualSnapshot,
    NativeDwgInRawSnapshot DwgInRaw);

internal readonly record struct NativeDbcsDecodeUnit(byte FirstByte, byte SecondByte, char Value);

internal readonly record struct NativeReadStringEvent(
    string OverloadName,
    int CodePageId,
    bool IsDoubleByte,
    int Status,
    uint ReturnRva,
    int StartByteOffset,
    int StartBitOffset,
    int EndByteOffset,
    int EndBitOffset,
    byte[] RawBytes,
    string Text);

internal readonly record struct NativeTextSetSourceEvent(
    uint ReturnRva,
    IntPtr SourcePointer,
    IntPtr DestinationPointer,
    int DestinationOffset,
    string Text,
    bool IsTruncated);

internal readonly record struct NativeFilerVirtualSnapshot(
    uint VTableRva,
    uint Method40Rva,
    uint Method88Rva,
    uint MethodB8Rva,
    uint MethodC0Rva,
    uint MethodC8Rva,
    uint Method188Rva,
    uint Method280Rva,
    uint Method290Rva)
{
    public static NativeFilerVirtualSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}

internal readonly record struct NativeDwgInRawSnapshot(
    int StartByteOffset,
    int StartBitOffset,
    int EndByteOffset,
    int EndBitOffset,
    byte[] RawBytes,
    bool IsTruncated)
{
    public static NativeDwgInRawSnapshot Empty { get; } = new(-1, -1, -1, -1, [], false);
}
#endif
