#if DEBUG
using System.Runtime.InteropServices;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DEBUG 专用 x64 inline hook 辅助器。
/// <para>
/// 仅用于 DBCS / code page 调查链路，负责安装绝对跳转、创建 trampoline，并在卸载时安全还原原始字节。
/// </para>
/// </summary>
/// <typeparam name="TDelegate">目标函数对应的托管委托类型。</typeparam>
internal sealed class NativeInlineHook<TDelegate> where TDelegate : Delegate
{
    private const uint MemCommit = 0x1000;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemReserveCommit = 0x3000;
    private const uint MemRelease = 0x8000;

    private readonly string _tag;
    private readonly string _name;
    private readonly uint _rva;

    private TDelegate? _hookDelegate;
    private IntPtr _targetAddr;
    private IntPtr _trampolineAddr;
    private IntPtr _hookStubAddr;
    private IntPtr _returnAddressStorage;
    private byte[]? _savedBytes;
    private int _prologueSize;

    /// <summary>
    /// 初始化 hook 辅助器。
    /// </summary>
    /// <param name="tag">诊断日志标签。</param>
    /// <param name="name">目标函数名称。</param>
    /// <param name="rva">目标函数 RVA。</param>
    public NativeInlineHook(string tag, string name, uint rva)
    {
        _tag = tag;
        _name = name;
        _rva = rva;
    }

    /// <summary>是否已成功安装。</summary>
    public bool IsInstalled { get; private set; }

    /// <summary>trampoline 委托。</summary>
    public TDelegate? TrampolineDelegate { get; private set; }

    /// <summary>最近一次 hook 入口捕获到的返回地址。</summary>
    public IntPtr CapturedReturnAddress
    {
        get
        {
            if (_returnAddressStorage == IntPtr.Zero || !IsCommittedMemory(_returnAddressStorage))
                return IntPtr.Zero;

            try { return Marshal.ReadIntPtr(_returnAddressStorage); }
            catch { return IntPtr.Zero; }
        }
    }

    /// <summary>
    /// 安装 inline hook。
    /// </summary>
    /// <param name="module">模块基址。</param>
    /// <param name="hookDelegate">替换目标函数的托管委托。</param>
    /// <param name="minPrologueSize">最少覆盖字节数。</param>
    /// <param name="maxPrologueSize">最大扫描字节数。</param>
    /// <param name="expectedPrefix">可选入口字节前缀，用于防止版本不匹配时误 patch。</param>
    /// <returns>成功返回 true。</returns>
    public bool Install(
        IntPtr module,
        TDelegate hookDelegate,
        int minPrologueSize,
        int maxPrologueSize,
        byte[]? expectedPrefix = null)
    {
        if (IsInstalled) return true;
        if (module == IntPtr.Zero) return false;

        IntPtr targetAddress = module + (int)_rva;
        return InstallAtAddress(targetAddress, _rva, hookDelegate, minPrologueSize, maxPrologueSize, expectedPrefix);
    }

    /// <summary>
    /// 在已解析的函数地址安装 inline hook。
    /// </summary>
    /// <param name="targetAddress">目标函数入口地址。</param>
    /// <param name="resolvedRva">目标函数在模块中的实际 RVA，仅用于诊断。</param>
    /// <param name="hookDelegate">替换目标函数的托管委托。</param>
    /// <param name="minPrologueSize">最少覆盖字节数。</param>
    /// <param name="maxPrologueSize">最大扫描字节数。</param>
    /// <param name="expectedPrefix">可选入口字节前缀，用于防止版本不匹配时误 patch。</param>
    /// <returns>成功返回 true。</returns>
    public bool InstallAtAddress(
        IntPtr targetAddress,
        uint resolvedRva,
        TDelegate hookDelegate,
        int minPrologueSize,
        int maxPrologueSize,
        byte[]? expectedPrefix = null)
    {
        if (IsInstalled) return true;
        if (targetAddress == IntPtr.Zero) return false;

        if (!IsCommittedMemory(targetAddress))
        {
            DiagnosticLogger.Log(_tag, $"{_name} RVA=0x{resolvedRva:X} 内存无效，跳过安装。");
            return false;
        }

        if (expectedPrefix != null && !MatchesBytes(targetAddress, expectedPrefix))
        {
            string actual = TryReadBytes(targetAddress, expectedPrefix.Length);
            DiagnosticLogger.Log(_tag, $"{_name} RVA=0x{resolvedRva:X} 入口字节不匹配，实际: {actual}");
            return false;
        }

        try
        {
            int prologueSize = ScanPrologueSize(targetAddress, minPrologueSize, maxPrologueSize);
            if (prologueSize < minPrologueSize)
            {
                DiagnosticLogger.Log(_tag, $"{_name} RVA=0x{resolvedRva:X} 序言扫描失败。");
                return false;
            }

            _prologueSize = prologueSize;
            _targetAddr = targetAddress;
            _savedBytes = new byte[_prologueSize];
            Marshal.Copy(_targetAddr, _savedBytes, 0, _prologueSize);

            _trampolineAddr = NativeInlineHookInterop.VirtualAlloc(IntPtr.Zero, (uint)(_prologueSize + 14), MemReserveCommit, PageExecuteReadWrite);
            if (_trampolineAddr == IntPtr.Zero)
            {
                DiagnosticLogger.Log(_tag, $"{_name} trampoline 分配失败。");
                ResetState();
                return false;
            }

            Marshal.Copy(_savedBytes, 0, _trampolineAddr, _prologueSize);
            WriteAbsoluteJump(_trampolineAddr + _prologueSize, _targetAddr + _prologueSize);
            TrampolineDelegate = Marshal.GetDelegateForFunctionPointer<TDelegate>(_trampolineAddr);

            _hookDelegate = hookDelegate;
            IntPtr hookFuncPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);
            _hookStubAddr = CreateReturnAddressCaptureStub(hookFuncPtr, out _returnAddressStorage);

            NativeInlineHookInterop.VirtualProtect(_targetAddr, (uint)_prologueSize, PageExecuteReadWrite, out uint oldProtect);
            byte[] patch = new byte[_prologueSize];
            for (int i = 0; i < patch.Length; i++) patch[i] = 0x90;
            WriteAbsoluteJumpBytes(patch, 0, _hookStubAddr);
            Marshal.Copy(patch, 0, _targetAddr, _prologueSize);
            NativeInlineHookInterop.VirtualProtect(_targetAddr, (uint)_prologueSize, oldProtect, out _);

            IsInstalled = true;
            DiagnosticLogger.Log(_tag, $"{_name} Hook 安装成功。RVA=0x{resolvedRva:X}, PrologueSize={_prologueSize}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"{_tag}: {_name} Hook 安装失败", ex);
            Uninstall();
            return false;
        }
    }

    /// <summary>卸载 hook 并还原原始字节。</summary>
    public void Uninstall()
    {
        try
        {
            if (IsInstalled
                && _targetAddr != IntPtr.Zero
                && _savedBytes != null
                && _savedBytes.Length == _prologueSize
                && IsCommittedMemory(_targetAddr))
            {
                NativeInlineHookInterop.VirtualProtect(_targetAddr, (uint)_prologueSize, PageExecuteReadWrite, out uint oldProtect);
                Marshal.Copy(_savedBytes, 0, _targetAddr, _prologueSize);
                NativeInlineHookInterop.VirtualProtect(_targetAddr, (uint)_prologueSize, oldProtect, out _);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"{_tag}: {_name} Hook 卸载失败", ex);
        }
        finally
        {
            ResetState();
        }
    }

    private void ResetState()
    {
        if (_trampolineAddr != IntPtr.Zero)
        {
            try { NativeInlineHookInterop.VirtualFree(_trampolineAddr, 0, MemRelease); } catch { }
        }

        if (_hookStubAddr != IntPtr.Zero)
        {
            try { NativeInlineHookInterop.VirtualFree(_hookStubAddr, 0, MemRelease); } catch { }
        }

        if (_returnAddressStorage != IntPtr.Zero)
        {
            try { NativeInlineHookInterop.VirtualFree(_returnAddressStorage, 0, MemRelease); } catch { }
        }

        IsInstalled = false;
        _targetAddr = IntPtr.Zero;
        _trampolineAddr = IntPtr.Zero;
        _hookStubAddr = IntPtr.Zero;
        _returnAddressStorage = IntPtr.Zero;
        _savedBytes = null;
        _prologueSize = 0;
        _hookDelegate = null;
        TrampolineDelegate = null;
    }

    private static bool MatchesBytes(IntPtr address, byte[] expected)
    {
        try
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (Marshal.ReadByte(address + i) != expected[i])
                    return false;
            }

            return true;
        }
        catch { return false; }
    }

    private static bool IsCommittedMemory(IntPtr address)
    {
        try
        {
            return NativeInlineHookInterop.VirtualQuery(address, out NativeInlineHookInterop.MemoryBasicInformation info, (uint)Marshal.SizeOf<NativeInlineHookInterop.MemoryBasicInformation>()) != IntPtr.Zero
                && info.State == MemCommit;
        }
        catch { return false; }
    }

    private static int ScanPrologueSize(IntPtr funcEntry, int minSize, int maxSize)
    {
        int offset = 0;
        try
        {
            while (offset < maxSize)
            {
                if (offset >= minSize) return offset;
                int instrLen = EstimateX64PrologueInstructionLength(funcEntry + offset);
                if (instrLen <= 0) return offset >= minSize ? offset : 0;
                offset += instrLen;
            }
        }
        catch { }

        return offset >= minSize ? offset : 0;
    }

    private static int EstimateX64PrologueInstructionLength(IntPtr addr)
    {
        try
        {
            byte b0 = Marshal.ReadByte(addr);
            byte b1 = Marshal.ReadByte(addr + 1);
            byte b2 = Marshal.ReadByte(addr + 2);
            byte b3 = Marshal.ReadByte(addr + 3);

            if (b0 == 0x90 || (b0 >= 0x50 && b0 <= 0x5F)) return 1;
            if (b0 == 0xC3) return 1;
            if (b0 == 0xC2) return 3;
            if (b0 == 0xE8 || b0 == 0xE9) return 5;
            if (b0 == 0xFF && b1 == 0x25) return 6;
            if (b0 == 0x6A) return 2;
            if (b0 == 0x68) return 5;
            if (b0 == 0x33 && (b1 >> 6) == 3) return 2;
            if (b0 == 0x85 && (b1 >> 6) == 3) return 2;
            if (b0 >= 0xB8 && b0 <= 0xBF) return 5;

            if (b0 >= 0x40 && b0 <= 0x4F)
            {
                if (b1 >= 0x50 && b1 <= 0x57) return 2;
                if ((b0 == 0x48 || b0 == 0x49) && b1 >= 0xB8 && b1 <= 0xBF) return 10;
                if (b0 == 0x48 && (b1 == 0xA1 || b1 == 0xA3)) return 10;
                if (b0 == 0x48 && b1 == 0x83 && (b2 == 0xEC || b2 == 0xC4)) return 4;
                if (b0 == 0x48 && b1 == 0x81 && (b2 == 0xEC || b2 == 0xC4)) return 7;
                if (b1 == 0x89 && b2 >= 0x40 && b2 <= 0x7F && b3 == 0x24) return 5;
                if (b1 == 0x89 && b2 >= 0x80 && b2 <= 0xBF && b3 == 0x24) return 8;
                if (b1 == 0x8B && b2 >= 0x40 && b2 <= 0x7F && b3 == 0x24) return 5;
                if (b1 == 0x8B && b2 >= 0x80 && b2 <= 0xBF && b3 == 0x24) return 8;
                if ((b1 == 0x8B || b1 == 0x89 || b1 == 0x63)
                    && (b2 >> 6) == 1
                    && (b2 & 0x07) != 4)
                    return 4;
                if ((b1 == 0x8B || b1 == 0x89 || b1 == 0x63)
                    && (b2 >> 6) == 2
                    && (b2 & 0x07) != 4)
                    return 7;
                if ((b0 == 0x48 || b0 == 0x49 || b0 == 0x4C || b0 == 0x4D)
                    && (b1 == 0x8B || b1 == 0x89)
                    && (b2 >> 6) == 3)
                    return 3;
                if (b0 == 0x4C && b1 == 0x8B && b2 == 0xDC) return 3;
                if (b0 == 0x48 && b1 == 0x8D && b2 >= 0x40 && b2 <= 0x7F && b3 == 0x24) return 5;
                if (b0 == 0x48 && b1 == 0x8D && b2 >= 0x80 && b2 <= 0xBF && b3 == 0x24) return 8;
                if ((b0 == 0x48 || b0 == 0x4C) && b1 == 0x8D && (b2 >> 6) == 0 && (b2 & 0x07) == 5) return 7;
                if ((b0 == 0x48 || b0 == 0x49) && b1 == 0x33 && (b2 >> 6) == 3) return 3;
                if (b0 == 0x48 && b1 == 0x85 && (b2 >> 6) == 3) return 3;
                if (b0 == 0x48 && b1 == 0xC7 && (b2 & 0xF8) == 0xC0) return 7;
                if (b0 == 0x48 && b1 == 0x3B && (b2 >> 6) == 3) return 3;
                if (b0 == 0x48 && b1 == 0x83 && ((b2 >> 3) & 0x07) == 7) return 4;
            }

            return 0;
        }
        catch { return 0; }
    }

    private static IntPtr CreateReturnAddressCaptureStub(IntPtr target, out IntPtr returnAddressStorage)
    {
        returnAddressStorage = NativeInlineHookInterop.VirtualAlloc(IntPtr.Zero, (uint)IntPtr.Size, MemReserveCommit, PageExecuteReadWrite);
        if (returnAddressStorage == IntPtr.Zero)
            throw new InvalidOperationException("无法分配返回地址存储内存。");

        IntPtr stub = NativeInlineHookInterop.VirtualAlloc(IntPtr.Zero, 64, MemReserveCommit, PageExecuteReadWrite);
        if (stub == IntPtr.Zero)
        {
            NativeInlineHookInterop.VirtualFree(returnAddressStorage, 0, MemRelease);
            returnAddressStorage = IntPtr.Zero;
            throw new InvalidOperationException("无法分配 Hook Stub 内存。");
        }

        byte[] code =
        [
            0x50,
            0x48, 0x8B, 0x44, 0x24, 0x08,
            0x48, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0,
            0x58,
            0xFF, 0x25, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        ];
        BitConverter.GetBytes(returnAddressStorage.ToInt64()).CopyTo(code, 8);
        BitConverter.GetBytes(target.ToInt64()).CopyTo(code, 23);
        Marshal.Copy(code, 0, stub, code.Length);
        return stub;
    }

    private static void WriteAbsoluteJump(IntPtr location, IntPtr target)
    {
        byte[] jmp = new byte[14];
        WriteAbsoluteJumpBytes(jmp, 0, target);
        Marshal.Copy(jmp, 0, location, 14);
    }

    private static void WriteAbsoluteJumpBytes(byte[] buffer, int offset, IntPtr target)
    {
        buffer[offset] = 0xFF;
        buffer[offset + 1] = 0x25;
        buffer[offset + 2] = 0x00;
        buffer[offset + 3] = 0x00;
        buffer[offset + 4] = 0x00;
        buffer[offset + 5] = 0x00;
        BitConverter.GetBytes(target.ToInt64()).CopyTo(buffer, offset + 6);
    }

    private static string TryReadBytes(IntPtr address, int count)
    {
        try
        {
            var buffer = new byte[count];
            Marshal.Copy(address, buffer, 0, count);
            return BitConverter.ToString(buffer).Replace('-', ' ');
        }
        catch (Exception ex)
        {
            return "<读取失败:" + ex.Message + ">";
        }
    }

}

/// <summary>
/// 非泛型 P/Invoke 容器。
/// </summary>
internal static class NativeInlineHookInterop
{
    /// <summary>更改内存保护属性。</summary>
    [DllImport("kernel32.dll")]
    public static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    /// <summary>查询虚拟内存区域。</summary>
    [DllImport("kernel32.dll")]
    public static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, uint dwLength);

    /// <summary>分配虚拟内存。</summary>
    [DllImport("kernel32.dll")]
    public static extern IntPtr VirtualAlloc(IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    /// <summary>释放虚拟内存。</summary>
    [DllImport("kernel32.dll")]
    public static extern bool VirtualFree(IntPtr lpAddress, uint dwSize, uint dwFreeType);

    /// <summary>读取模块导出函数地址。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    /// <summary>虚拟内存区域信息。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        /// <summary>区域基址。</summary>
        public IntPtr BaseAddress;
        /// <summary>分配基址。</summary>
        public IntPtr AllocationBase;
        /// <summary>初始保护属性。</summary>
        public uint AllocationProtect;
        /// <summary>区域大小。</summary>
        public IntPtr RegionSize;
        /// <summary>提交状态。</summary>
        public uint State;
        /// <summary>当前保护属性。</summary>
        public uint Protect;
        /// <summary>区域类型。</summary>
        public uint Type;
    }
}

/// <summary>
/// 已加载 PE 模块的 DEBUG 签名解析器。
/// </summary>
internal static class NativeModulePatternScanner
{
    private const ushort DosSignature = 0x5A4D;
    private const uint NtSignature = 0x00004550;

    /// <summary>
    /// 在已加载模块的 <c>.text</c> 段查找唯一字节签名。
    /// </summary>
    /// <param name="module">模块基址。</param>
    /// <param name="pattern">签名字节；使用 -1 表示通配字节。</param>
    /// <param name="tag">诊断日志标签。</param>
    /// <param name="name">目标名称。</param>
    /// <param name="address">匹配到的入口地址。</param>
    /// <param name="rva">匹配到的 RVA。</param>
    /// <returns>唯一匹配时返回 true。</returns>
    public static bool TryFindUniqueTextPattern(
        IntPtr module,
        int[] pattern,
        string tag,
        string name,
        out IntPtr address,
        out uint rva)
    {
        address = IntPtr.Zero;
        rva = 0;

        if (module == IntPtr.Zero || pattern.Length == 0)
            return false;

        try
        {
            if (!TryGetTextSection(module, out uint textRva, out int textSize))
            {
                DiagnosticLogger.Log(tag, $"{name} 无法解析 .text 段，跳过安装。");
                return false;
            }

            var text = new byte[textSize];
            Marshal.Copy(module + (int)textRva, text, 0, text.Length);

            int hitCount = 0;
            int firstOffset = -1;
            string hitRvas = "";
            int lastStart = text.Length - pattern.Length;
            for (int i = 0; i <= lastStart; i++)
            {
                if (!Matches(text, i, pattern))
                    continue;

                hitCount++;
                if (firstOffset < 0)
                    firstOffset = i;

                if (hitCount <= 8)
                    hitRvas += (hitRvas.Length == 0 ? "" : ", ") + "0x" + (textRva + i).ToString("X");
            }

            if (hitCount != 1)
            {
                DiagnosticLogger.Log(tag,
                    $"{name} 签名匹配数={hitCount}，跳过安装。Hits={hitRvas}");
                return false;
            }

            rva = textRva + (uint)firstOffset;
            address = module + (int)rva;
            DiagnosticLogger.Log(tag, $"{name} 签名解析成功。RVA=0x{rva:X}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"{tag}: {name} 签名解析失败", ex);
            return false;
        }
    }

    private static bool TryGetTextSection(IntPtr module, out uint textRva, out int textSize)
    {
        textRva = 0;
        textSize = 0;

        if ((ushort)Marshal.ReadInt16(module) != DosSignature)
            return false;

        int peOffset = Marshal.ReadInt32(module + 0x3C);
        IntPtr ntHeader = module + peOffset;
        if ((uint)Marshal.ReadInt32(ntHeader) != NtSignature)
            return false;

        ushort sectionCount = (ushort)Marshal.ReadInt16(ntHeader + 6);
        ushort optionalHeaderSize = (ushort)Marshal.ReadInt16(ntHeader + 20);
        IntPtr sectionHeader = ntHeader + 24 + optionalHeaderSize;

        for (int i = 0; i < sectionCount; i++)
        {
            IntPtr section = sectionHeader + (i * 40);
            string name = ReadSectionName(section);
            if (!name.Equals(".text", StringComparison.Ordinal))
                continue;

            uint virtualSize = (uint)Marshal.ReadInt32(section + 8);
            textRva = (uint)Marshal.ReadInt32(section + 12);
            textSize = checked((int)virtualSize);
            return textRva != 0 && textSize > 0;
        }

        return false;
    }

    private static string ReadSectionName(IntPtr section)
    {
        Span<byte> bytes = stackalloc byte[8];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Marshal.ReadByte(section + i);

        int length = 0;
        while (length < bytes.Length && bytes[length] != 0)
            length++;

        return System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }

    private static bool Matches(byte[] buffer, int offset, int[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            int expected = pattern[i];
            if (expected >= 0 && buffer[offset + i] != expected)
                return false;
        }

        return true;
    }
}
#endif
