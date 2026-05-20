using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// TextEditor DBCS 字节解码 Hook。
/// <para>
/// 单行文字的 DWG 读取路径可能绕过 <see cref="CodePageFamilyHook"/> 覆盖的 context 初始化函数。
/// 本 Hook 只拦截序言可安全 trampoline 的 TextEditor DBCS 入口，在当前线程处于 DBText/DWG filer
/// 作用域时记录原始 code page 与 filer code page mismatch，不改写传入 code page。
/// </para>
/// </summary>
internal static class TextEditorDbcsDecodeHook
{
    private const string Tag = "TextEditorDbcs";
    private const uint MemCommit = 0x1000;
    private const int EvidenceLogLimit = 40;
    private const int NoScopeLogLimit = 8;
    private const int DecodeProbeLogLimit = 24;
    private const int DecodeEvidenceSampleLimit = 16;

    private static NativeInlineHook<ReadDoubleByteAnsiDelegate>? _readDoubleByteHook;
    private static NativeInlineHook<MultiByteToUnicodeAcStringDelegate>? _multiByteToUnicodeAcStringHook;
    private static TextEditorDbcsDecodeHookProfile? _profile;
    private static IntPtr _moduleBase;
    private static bool _installed;
    private static int _hitCount;
    private static int _readDoubleByteHitCount;
    private static int _multiByteToUnicodeAcStringHitCount;
    private static int _mismatchEvidenceCount;
    private static int _noScopeCount;
    private static int _sameCodePageCount;
    private static int _noScopeLogCount;
    private static int _decodeProbeLogCount;
    private static int _asciiProbeSkipCount;
#if !NETFRAMEWORK
    private static int _codePageProviderRegistered;
#endif
    private static readonly object DecodeEvidenceLock = new();
    private static readonly Dictionary<int, char> DecodeEvidence = new();
    private static int _lastOriginalCodePageId;
    private static int _lastFilerCodePageId;
    private static int _lastEvidenceCodePageId;
    private static uint _lastReturnRva;
    private static string _lastApiName = string.Empty;
    [ThreadStatic] private static bool _inHook;

    /// <summary>
    /// <c>TextEditor::read_doublebyte(char const*, wchar_t&amp;, code_page_id)</c> 委托。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ReadDoubleByteAnsiDelegate(IntPtr input, IntPtr outputChar, int codePageId);

    /// <summary>
    /// <c>TextEditor::MultiByteToUnicode(char const*, int, code_page_id, AcString&amp;)</c> 委托。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool MultiByteToUnicodeAcStringDelegate(IntPtr input, int length, int codePageId, IntPtr output);

    /// <summary>安装 TextEditor DBCS 解码 Hook。</summary>
    public static void Install()
    {
        if (_installed) return;

        if (!NativeDecodeHookProfileResolver.TryGetCurrent(Tag, out var nativeProfile))
            return;

        var profile = nativeProfile.TextEditorDbcsDecode;
        _profile = profile;
        if (!profile.HasInstallableHook)
        {
            DiagnosticLogger.Log(Tag, $"{nativeProfile.PlatformName}: {profile.ReadDoubleByteAnsi.DisabledReason ?? profile.MultiByteToUnicodeAcString.DisabledReason ?? nativeProfile.SupportNote}");
            return;
        }

        _moduleBase = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (_moduleBase == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过安装。");
            return;
        }

        bool installed = TryInstallReadDoubleByteHook(profile.ReadDoubleByteAnsi);
        installed |= TryInstallMultiByteToUnicodeAcStringHook(profile.MultiByteToUnicodeAcString);
        _installed = installed;
    }

    /// <summary>卸载 TextEditor DBCS 解码 Hook。</summary>
    public static void Uninstall()
    {
        _readDoubleByteHook?.Uninstall();
        _multiByteToUnicodeAcStringHook?.Uninstall();
        _readDoubleByteHook = null;
        _multiByteToUnicodeAcStringHook = null;
        _profile = null;
        _moduleBase = IntPtr.Zero;
        _installed = false;
        DiagnosticLogger.Log(Tag,
            $"已卸载。HitCount={_hitCount}, ReadDoubleByteHits={_readDoubleByteHitCount}, " +
            $"MultiByteToUnicodeHits={_multiByteToUnicodeAcStringHitCount}, " +
            $"MismatchEvidence={_mismatchEvidenceCount}, NoScope={_noScopeCount}");
    }

    /// <summary>获取诊断报告。</summary>
    public static string GetReport()
    {
        return string.Join(Environment.NewLine,
            "=== TextEditor DBCS Decode Hook ===",
            $"Installed: {_installed}",
            $"HitCount: {_hitCount}",
            $"ReadDoubleByteHitCount: {_readDoubleByteHitCount}",
            $"MultiByteToUnicodeAcStringHitCount: {_multiByteToUnicodeAcStringHitCount}",
            $"MismatchEvidenceCount: {_mismatchEvidenceCount}",
            $"NoDbcsScopeCount: {_noScopeCount}",
            $"SameCodePageCount: {_sameCodePageCount}",
            $"AsciiProbeSkipCount: {_asciiProbeSkipCount}",
            $"ObservedDecodeEvidenceCount: {GetDecodeEvidenceCount()}",
            $"ObservedDecodeEvidenceSamples: {FormatDecodeEvidenceSamples()}",
            $"LastApiName: {_lastApiName}",
            $"LastReturnRva: 0x{_lastReturnRva:X}",
            $"LastOriginalCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastOriginalCodePageId)}",
            $"LastFilerCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastFilerCodePageId)}",
            $"LastEvidenceCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastEvidenceCodePageId)}");
    }

    /// <summary>
    /// 使用当前运行时已观察到的 native 双字节解码证据，尝试预览乱码字符串的无猜测恢复结果。
    /// </summary>
    /// <param name="text">已进入托管层的字符串。</param>
    /// <param name="codePageId">native DWG 读入阶段为当前对象记录的 AutoCAD code_page_id。</param>
    /// <param name="decoded">全部双字节均有 native 证据时的恢复结果。</param>
    /// <param name="reason">无法完整恢复时的覆盖原因。</param>
    /// <returns>所有非 ASCII 双字节均有 native 证据时返回 true。</returns>
    public static bool TryDecodeWithObservedEvidence(
        string text,
        int codePageId,
        out string decoded,
        out string reason,
        bool allowNativeExpansion = true)
    {
        decoded = string.Empty;
        reason = "<empty>";

        if (string.IsNullOrEmpty(text))
            return false;

        if (!_installed || codePageId == 0)
        {
            reason = "no-native-codepage-evidence";
            return false;
        }

        EnsureCodePageProviderRegistered();
        Encoding byteCarrier = Encoding.GetEncoding(
            950,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

        var builder = new StringBuilder(text.Length);
        int dbcsCount = 0;
        int missingCount = 0;
        int nativeExpansionCount = 0;
        var missing = new List<string>();

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x7F)
            {
                builder.Append(ch);
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = byteCarrier.GetBytes(new[] { ch });
            }
            catch
            {
                missingCount++;
                if (missing.Count < 8)
                    missing.Add($"U+{(int)ch:X4}");
                builder.Append(ch);
                continue;
            }

            if (bytes.Length != 2 || bytes[0] < 0x80)
            {
                builder.Append(ch);
                continue;
            }

            dbcsCount++;
            int key = BuildDecodeEvidenceKey(codePageId, bytes[0], bytes[1]);
            if (TryGetDecodeEvidence(key, out char mapped))
            {
                builder.Append(mapped);
                continue;
            }

            if (allowNativeExpansion
                && TryDecodeBytesWithNative(codePageId, bytes[0], bytes[1], out mapped))
            {
                nativeExpansionCount++;
                builder.Append(mapped);
                continue;
            }

            missingCount++;
            if (missing.Count < 8)
                missing.Add($"{bytes[0]:X2} {bytes[1]:X2}");
            builder.Append(ch);
        }

        decoded = builder.ToString();
        if (dbcsCount == 0)
        {
            reason = "no-dbcs-bytes";
            return false;
        }

        if (missingCount != 0)
        {
            reason = $"partial dbcs={dbcsCount}, missing={missingCount}, samples={string.Join(",", missing)}";
            return false;
        }

        reason = nativeExpansionCount == 0
            ? $"full dbcs={dbcsCount}"
            : $"full dbcs={dbcsCount}, native-expanded={nativeExpansionCount}";
        return !string.Equals(text, decoded, StringComparison.Ordinal);
    }

    /// <summary>
    /// Build the byte carrier used by the observed fallback path.
    /// <para>
    /// This is diagnostic-only evidence. The bytes are derived from the current managed Unicode
    /// string by encoding it as CP950, so they are not native/DWG source bytes unless another
    /// probe independently shows the same full byte sequence.
    /// </para>
    /// </summary>
    public static bool TryBuildObservedCarrierBytes(string text, out byte[] carrierBytes, out string reason)
    {
        carrierBytes = [];
        reason = "<empty>";

        if (string.IsNullOrEmpty(text))
            return false;

        EnsureCodePageProviderRegistered();
        Encoding byteCarrier = Encoding.GetEncoding(
            950,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

        var bytesOut = new List<byte>(text.Length * 2);
        var missing = new List<string>();
        int asciiCount = 0;
        int dbcsCount = 0;
        int nonDbcsEncodedCount = 0;

        foreach (char ch in text)
        {
            if (ch <= 0x7F)
            {
                bytesOut.Add((byte)ch);
                asciiCount++;
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = byteCarrier.GetBytes(new[] { ch });
            }
            catch
            {
                if (missing.Count < 8)
                    missing.Add($"U+{(int)ch:X4}");
                continue;
            }

            bytesOut.AddRange(bytes);
            if (bytes.Length == 2 && bytes[0] >= 0x80)
                dbcsCount++;
            else
                nonDbcsEncodedCount++;
        }

        carrierBytes = bytesOut.ToArray();
        if (missing.Count != 0)
        {
            reason =
                $"partial observed-carrier cp950 ascii={asciiCount}, dbcs={dbcsCount}, " +
                $"nonDbcs={nonDbcsEncodedCount}, missing={missing.Count}, samples={string.Join(",", missing)}";
            return false;
        }

        if (dbcsCount == 0)
        {
            reason = $"no-dbcs-carrier-bytes cp950 ascii={asciiCount}, nonDbcs={nonDbcsEncodedCount}";
            return false;
        }

        reason =
            $"observed-carrier cp950 ascii={asciiCount}, dbcs={dbcsCount}, " +
            $"nonDbcs={nonDbcsEncodedCount}, bytes={carrierBytes.Length}";
        return true;
    }

    /// <summary>
    /// 使用最近一次 native code page 证据做只读诊断预览。
    /// <para>
    /// 该方法仅供 Inspect 命令显示候选预览；自动写回必须传入对象级 provenance code page。
    /// </para>
    /// </summary>
    public static bool TryPreviewWithLastObservedEvidence(
        string text,
        out string decoded,
        out string reason,
        bool allowNativeExpansion = true)
    {
        return TryDecodeWithObservedEvidence(
            text,
            _lastOriginalCodePageId,
            out decoded,
            out reason,
            allowNativeExpansion);
    }

    private static bool TryInstallReadDoubleByteHook(NativeHookTarget target)
    {
        if (!TryGetExportAddress(
                _moduleBase,
                target,
                out IntPtr address,
                out uint rva))
            return false;

        _readDoubleByteHook = new NativeInlineHook<ReadDoubleByteAnsiDelegate>(
            Tag,
            "TextEditor::read_doublebyte(char*)",
            rva);
        return _readDoubleByteHook.InstallAtAddress(
            address,
            rva,
            ReadDoubleByteAnsiHookHandler,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static bool TryInstallMultiByteToUnicodeAcStringHook(NativeHookTarget target)
    {
        if (!TryGetExportAddress(
                _moduleBase,
                target,
                out IntPtr address,
                out uint rva))
            return false;

        _multiByteToUnicodeAcStringHook = new NativeInlineHook<MultiByteToUnicodeAcStringDelegate>(
            Tag,
            "TextEditor::MultiByteToUnicode(char*,AcString)",
            rva);
        return _multiByteToUnicodeAcStringHook.InstallAtAddress(
            address,
            rva,
            MultiByteToUnicodeAcStringHookHandler,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static bool ReadDoubleByteAnsiHookHandler(IntPtr input, IntPtr outputChar, int codePageId)
    {
        var trampoline = _readDoubleByteHook?.TrampolineDelegate;
        if (trampoline == null)
            return false;

        if (_inHook)
            return trampoline(input, outputChar, codePageId);

        _inHook = true;
        try
        {
            uint returnRva = GetReturnRva(_readDoubleByteHook?.CapturedReturnAddress ?? IntPtr.Zero);
            int evidenceCodePageId = ResolveCodePageForCall(
                "read_doublebyte",
                returnRva,
                codePageId,
                ref _readDoubleByteHitCount,
                out bool hasDbcsScope);
            bool result = trampoline(input, outputChar, codePageId);
            TryRecordScopedDbTextDecode(input, outputChar, codePageId, hasDbcsScope, result);
            TryLogReadDoubleByteProbe(returnRva, input, outputChar, codePageId, evidenceCodePageId, hasDbcsScope, result);
            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": read_doublebyte HookHandler 异常", ex);
            return trampoline(input, outputChar, codePageId);
        }
        finally
        {
            _inHook = false;
        }
    }

    private static bool MultiByteToUnicodeAcStringHookHandler(IntPtr input, int length, int codePageId, IntPtr output)
    {
        var trampoline = _multiByteToUnicodeAcStringHook?.TrampolineDelegate;
        if (trampoline == null)
            return false;

        if (_inHook)
            return trampoline(input, length, codePageId, output);

        _inHook = true;
        try
        {
            uint returnRva = GetReturnRva(_multiByteToUnicodeAcStringHook?.CapturedReturnAddress ?? IntPtr.Zero);
            int evidenceCodePageId = ResolveCodePageForCall(
                "MultiByteToUnicode(char*,AcString)",
                returnRva,
                codePageId,
                ref _multiByteToUnicodeAcStringHitCount,
                out bool hasDbcsScope);
            bool result = trampoline(input, length, codePageId, output);
            TryLogMultiByteToUnicodeProbe(returnRva, input, length, codePageId, evidenceCodePageId, hasDbcsScope, result);
            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": MultiByteToUnicode HookHandler 异常", ex);
            return trampoline(input, length, codePageId, output);
        }
        finally
        {
            _inHook = false;
        }
    }

    private static int ResolveCodePageForCall(
        string apiName,
        uint returnRva,
        int codePageId,
        ref int apiHitCount,
        out bool hasDbcsScope)
    {
        Interlocked.Increment(ref _hitCount);
        Interlocked.Increment(ref apiHitCount);
        _lastApiName = apiName;
        _lastReturnRva = returnRva;
        _lastOriginalCodePageId = codePageId;
        hasDbcsScope = false;

        if (!DwgFilerCodePageScopeHook.TryGetCurrentDbcsCodePageId(out int filerCodePageId))
        {
            Interlocked.Increment(ref _noScopeCount);
            if (Interlocked.Increment(ref _noScopeLogCount) <= NoScopeLogLimit)
            {
                DiagnosticLogger.Log(Tag,
                    $"{apiName}: return=0x{returnRva:X}, no readString DBCS scope, " +
                    $"codePage={DwgFilerCodePageScopeHook.FormatCodePageId(codePageId)}");
            }

            return codePageId;
        }

        hasDbcsScope = true;
        _lastFilerCodePageId = filerCodePageId;
        if (codePageId == filerCodePageId)
        {
            Interlocked.Increment(ref _sameCodePageCount);
            return codePageId;
        }

        _lastEvidenceCodePageId = filerCodePageId;
        int evidenceCount = Interlocked.Increment(ref _mismatchEvidenceCount);
        DbTextDwgInFieldsScopeHook.RecordCodePageFamilyEvidence(codePageId, filerCodePageId);
        if (evidenceCount <= EvidenceLogLimit)
        {
            DiagnosticLogger.Log(Tag,
                $"证据命中 {apiName}: return=0x{returnRva:X}, " +
                $"{DwgFilerCodePageScopeHook.FormatCodePageId(codePageId)} -> " +
                $"{DwgFilerCodePageScopeHook.FormatCodePageId(filerCodePageId)}");
        }

        return filerCodePageId;
    }

    private static void TryLogReadDoubleByteProbe(
        uint returnRva,
        IntPtr input,
        IntPtr outputChar,
        int originalCodePageId,
        int evidenceCodePageId,
        bool hasDbcsScope,
        bool result)
    {
        if (hasDbcsScope)
            return;

        try
        {
            byte b0 = ReadByteSafe(input, 0);
            byte b1 = ReadByteSafe(input, 1);
            char output = '\0';
            if (result && outputChar != IntPtr.Zero && IsCommittedMemory(outputChar))
            {
                output = (char)Marshal.ReadInt16(outputChar);
                RememberDecodeEvidence(originalCodePageId, b0, b1, output);
            }

            if (!result && b0 < 0x80 && b1 < 0x80)
            {
                Interlocked.Increment(ref _asciiProbeSkipCount);
                return;
            }

            if (Interlocked.Increment(ref _decodeProbeLogCount) > DecodeProbeLogLimit)
                return;

            byte[] bytes = b1 == 0 ? [b0] : [b0, b1];
            DiagnosticLogger.Log(Tag,
                $"no-scope read_doublebyte probe: return=0x{returnRva:X}, " +
                $"bytes={FormatBytes(bytes)}, original={DwgFilerCodePageScopeHook.FormatCodePageId(originalCodePageId)}, " +
                $"evidence={DwgFilerCodePageScopeHook.FormatCodePageId(evidenceCodePageId)}, " +
                $"nativeResult={result}, nativeChar={FormatChar(output)}, " +
                $"cp950={FormatDecoded(bytes, 950)}, cp936={FormatDecoded(bytes, 936)}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": read_doublebyte probe 失败", ex);
        }
    }

    internal static bool TryDecodeNativeBytePair(int codePageId, byte firstByte, byte secondByte, out char mapped)
    {
        return TryDecodeBytesWithNative(codePageId, firstByte, secondByte, out mapped);
    }

    private static void TryRecordScopedDbTextDecode(IntPtr input, IntPtr outputChar, int appliedCodePageId, bool hasDbcsScope, bool result)
    {
        if (!hasDbcsScope || !result || outputChar == IntPtr.Zero || !IsCommittedMemory(outputChar))
            return;

        byte firstByte = ReadByteSafe(input, 0);
        byte secondByte = ReadByteSafe(input, 1);
        char output = (char)Marshal.ReadInt16(outputChar);
        DbTextDwgInFieldsScopeHook.RecordNativeDecodedDbcsChar(firstByte, secondByte, output, appliedCodePageId);
    }

    private static void TryLogMultiByteToUnicodeProbe(
        uint returnRva,
        IntPtr input,
        int length,
        int originalCodePageId,
        int evidenceCodePageId,
        bool hasDbcsScope,
        bool result)
    {
        if (hasDbcsScope)
            return;

        try
        {
            int sampleLength = length < 0 ? 0 : Math.Min(length, 24);
            byte[] bytes = ReadBytesSafe(input, sampleLength);
            if (!ContainsHighBit(bytes))
            {
                Interlocked.Increment(ref _asciiProbeSkipCount);
                return;
            }

            if (Interlocked.Increment(ref _decodeProbeLogCount) > DecodeProbeLogLimit)
                return;

            DiagnosticLogger.Log(Tag,
                $"no-scope MultiByteToUnicode probe: return=0x{returnRva:X}, len={length}, " +
                $"bytes={FormatBytes(bytes)}, original={DwgFilerCodePageScopeHook.FormatCodePageId(originalCodePageId)}, " +
                $"evidence={DwgFilerCodePageScopeHook.FormatCodePageId(evidenceCodePageId)}, nativeResult={result}, " +
                $"cp950={FormatDecoded(bytes, 950)}, cp936={FormatDecoded(bytes, 936)}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": MultiByteToUnicode probe 失败", ex);
        }
    }

    private static byte ReadByteSafe(IntPtr address, int offset)
    {
        if (address == IntPtr.Zero || !IsCommittedMemory(address + offset))
            return 0;

        return Marshal.ReadByte(address, offset);
    }

    private static byte[] ReadBytesSafe(IntPtr address, int length)
    {
        if (address == IntPtr.Zero || length <= 0)
            return [];

        var bytes = new byte[length];
        int count = 0;
        for (; count < length; count++)
        {
            IntPtr current = address + count;
            if (!IsCommittedMemory(current))
                break;

            byte value = Marshal.ReadByte(current);
            if (value == 0)
                break;

            bytes[count] = value;
        }

        if (count == bytes.Length)
            return bytes;

        Array.Resize(ref bytes, count);
        return bytes;
    }

    private static string FormatBytes(byte[] bytes)
    {
        return bytes.Length == 0
            ? "<empty>"
            : string.Join(" ", bytes.Select(b => b.ToString("X2")));
    }

    private static bool ContainsHighBit(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] >= 0x80)
                return true;
        }

        return false;
    }

    private static void RememberDecodeEvidence(int codePageId, byte b0, byte b1, char output)
    {
        if (output == '\0' || b0 < 0x80)
            return;

        int key = BuildDecodeEvidenceKey(codePageId, b0, b1);
        lock (DecodeEvidenceLock)
        {
            if (!DecodeEvidence.ContainsKey(key))
                DecodeEvidence.Add(key, output);
        }
    }

    private static int GetDecodeEvidenceCount()
    {
        lock (DecodeEvidenceLock)
        {
            return DecodeEvidence.Count;
        }
    }

    private static bool TryGetDecodeEvidence(int key, out char value)
    {
        lock (DecodeEvidenceLock)
        {
            return DecodeEvidence.TryGetValue(key, out value);
        }
    }

    private static bool TryDecodeBytesWithNative(int originalCodePageId, byte b0, byte b1, out char mapped)
    {
        mapped = '\0';
        var trampoline = _readDoubleByteHook?.TrampolineDelegate;
        if (trampoline == null || originalCodePageId == 0)
            return false;

        IntPtr input = IntPtr.Zero;
        IntPtr output = IntPtr.Zero;
        bool previousInHook = _inHook;
        try
        {
            input = Marshal.AllocHGlobal(3);
            output = Marshal.AllocHGlobal(sizeof(char));
            Marshal.WriteByte(input, 0, b0);
            Marshal.WriteByte(input, 1, b1);
            Marshal.WriteByte(input, 2, 0);
            Marshal.WriteInt16(output, 0);

            _inHook = true;
            if (!trampoline(input, output, originalCodePageId))
                return false;

            mapped = (char)Marshal.ReadInt16(output);
            RememberDecodeEvidence(originalCodePageId, b0, b1, mapped);
            return mapped != '\0';
        }
        catch
        {
            mapped = '\0';
            return false;
        }
        finally
        {
            _inHook = previousInHook;
            if (input != IntPtr.Zero)
                Marshal.FreeHGlobal(input);
            if (output != IntPtr.Zero)
                Marshal.FreeHGlobal(output);
        }
    }

    private static string FormatDecodeEvidenceSamples()
    {
        lock (DecodeEvidenceLock)
        {
            if (DecodeEvidence.Count == 0)
                return "<none>";

            return string.Join(", ",
                DecodeEvidence
                    .OrderBy(item => item.Key)
                    .Take(DecodeEvidenceSampleLimit)
                    .Select(item =>
                    {
                        int codePageId = (item.Key >> 16) & 0xFFFF;
                        int b0 = (item.Key >> 8) & 0xFF;
                        int b1 = item.Key & 0xFF;
                        return $"{DwgFilerCodePageScopeHook.FormatCodePageId(codePageId)}:{b0:X2} {b1:X2}->{FormatChar(item.Value)}";
                    }));
        }
    }

    private static int BuildDecodeEvidenceKey(int codePageId, byte b0, byte b1)
    {
        return ((codePageId & 0xFFFF) << 16) | (b0 << 8) | b1;
    }

    private static string FormatDecoded(byte[] bytes, int windowsCodePage)
    {
        if (bytes.Length == 0)
            return "<empty>";

        try
        {
            EnsureCodePageProviderRegistered();
            string value = Encoding.GetEncoding(
                windowsCodePage,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback).GetString(bytes);
            return EscapeForLog(value);
        }
        catch (Exception ex)
        {
            return "<decode-failed:" + ex.GetType().Name + ">";
        }
    }

    private static string FormatChar(char value)
    {
        return value == '\0'
            ? "<none>"
            : $"U+{(int)value:X4} '{EscapeForLog(value.ToString())}'";
    }

    private static void EnsureCodePageProviderRegistered()
    {
#if !NETFRAMEWORK
        if (Interlocked.Exchange(ref _codePageProviderRegistered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
    }

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("'", "\\'");
    }

    private static bool TryGetExportAddress(
        IntPtr module,
        NativeHookTarget target,
        out IntPtr address,
        out uint rva)
    {
        string? exportName = target.ExportName;
        if (!target.IsEnabled || string.IsNullOrWhiteSpace(exportName))
        {
            address = IntPtr.Zero;
            rva = 0;
            DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：{target.DisabledReason ?? "缺少导出符号"}");
            return false;
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

    private static uint GetReturnRva(IntPtr returnAddress)
    {
        if (returnAddress == IntPtr.Zero || _moduleBase == IntPtr.Zero)
            return 0;

        long rva = returnAddress.ToInt64() - _moduleBase.ToInt64();
        if (rva <= 0 || rva > uint.MaxValue)
            return 0;

        return (uint)rva;
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
}
