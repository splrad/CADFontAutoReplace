#if DEBUG
using System.Runtime.InteropServices;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DBCS Code Page Family 误判修复 Hook。
/// <para>
/// 该类型仅在 DEBUG 构建中编译。通过 inline hook acdb25.dll 的 code-page family 字段写入函数，
/// 在 GBK 系统 locale 将文字对象（MText/DBText）的 code_page_id 字段错误写为 GBK family(0x28) 时，
/// 将其覆写为正确的 Big5 family(0x27)，防止后续 DBCS 字节解码走 GBK 路径产生乱码。
/// </para>
/// <para>
/// 根因：AutoCAD 启动时根据系统 locale 初始化 AcLocale 对象，其 code page family 缓存被全局写入。
/// 后续打开 Big5/CP950 图纸时，DWG 反序列化过程中该函数将 session-wide 的 GBK family 写入对象字段，
/// 导致 DBCS 字节被按 GBK 而非 Big5 解码。
/// </para>
/// </summary>
internal static class CodePageFamilyHook
{
    // Big5 code_page_id (0x27=39) 和 GBK code_page_id (0x28=40)
    private const int CodePageIdBig5 = 0x27;
    private const int CodePageIdGbk = 0x28;

    // 对象字段偏移：[AcDbImpText*/AcDbImpMText* + 0x46C] = code_page_id 字段
    private const int CodePageIdFieldOffset = 0x46C;

    // Hook 目标函数 RVA（acdb25.dll 当前版本）
    // 0x6CEED4 入口字节 D4 FD FF 90 表明不是标准 prologue
    // 尝试附近可能的函数入口对齐地址(x64 函数通常 16 字节对齐)
    // 已知 0x6CFE6C 可用但仅覆盖 MText
    private static readonly uint[] CandidateRvas = [
        0x6CEEC0,  // -0x14 from 0x6CEED4
        0x6CEEB0,  // -0x24
        0x6CEEA0,  // -0x34
        0x6CEE90,  // -0x44
        0x6CEE80,  // -0x54
        0x6CEE70,  // -0x64
        0x6CFE6C   // 已知可用 (MText only)
    ];

    private const uint MemCommit = 0x1000;

    // Hook 字段
    private static CodePageFamilyFixDelegate? _hookDelegate;
    private static CodePageFamilyFixDelegate? _trampolineDelegate;
    private static IntPtr _targetAddr;
    private static IntPtr _trampolineAddr;
    private static IntPtr _hookStubAddr;
    private static IntPtr _returnAddressStorage;
    private static byte[]? _savedBytes;
    private static bool _installed;
    private static int _hitCount;
    private static int _patchedCount;
    private static int _prologueSize;
    [ThreadStatic] private static bool _inHook;

    // 由外部在文档打开前预设，供 hook handler 只读
    private static volatile bool _currentDrawingIsBig5;

    /// <summary>
    /// 当前图纸是否为 Big5 图纸(公共只读属性,供 DbTextEncodingRepairService 使用)。
    /// </summary>
    public static bool CurrentDrawingIsBig5 => _currentDrawingIsBig5;

    /// <summary>
    /// code-page family 写入函数的委托类型。
    /// Windows x64 calling convention: rcx=this, rdx=arg2
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CodePageFamilyFixDelegate(IntPtr textObject, int arg2);

    /// <summary>
    /// code-page family 读取函数的委托类型 (0x6AC1A4)。
    /// 返回值: eax = code_page_id
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CodePageFamilyReaderDelegate();

    /// <summary>
    /// 安装 Code Page Family 修复 Hook。
    /// <para>在插件初始化阶段调用。</para>
    /// </summary>
    public static void Install()
    {
        if (_installed) return;

        string dllName = PlatformManager.Platform.AcDbDllName;
        if (!dllName.Equals("acdb25.dll", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticLogger.Log("CodePageHook", $"{dllName} 无预置 RVA，跳过安装。");
            return;
        }

        IntPtr module = GetModuleHandle(dllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Log("CodePageHook", $"{dllName} 未加载，跳过安装。");
            return;
        }

        InstallHook(module);
    }

    /// <summary>卸载 Hook。</summary>
    public static void Uninstall()
    {
        if (!_installed) return;

        try
        {
            if (_targetAddr != IntPtr.Zero && _savedBytes != null && _savedBytes.Length == _prologueSize)
            {
                VirtualProtect(_targetAddr, (uint)_prologueSize, 0x40, out uint oldProtect);
                Marshal.Copy(_savedBytes, 0, _targetAddr, _prologueSize);
                VirtualProtect(_targetAddr, (uint)_prologueSize, oldProtect, out _);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError("CodePageHook: 卸载失败", ex);
        }
        finally
        {
            if (_trampolineAddr != IntPtr.Zero)
            {
                try { VirtualFree(_trampolineAddr, 0, 0x8000); } catch { }
            }

            _installed = false;
            _targetAddr = IntPtr.Zero;
            _trampolineAddr = IntPtr.Zero;
            _prologueSize = 0;
            FreeHookStub(ref _hookStubAddr, ref _returnAddressStorage);
            _trampolineDelegate = null;
            _hookDelegate = null;
            _savedBytes = null;
        }

        _currentDrawingIsBig5 = false;
        DiagnosticLogger.Log("CodePageHook", $"已卸载。HitCount={_hitCount}, PatchedCount={_patchedCount}");
    }

    /// <summary>
    /// 设置当前图纸是否为 Big5 / CP950 页族。
    /// <para>
    /// 必须在 DocumentCreateStarted 事件中通过 TryIsDwgFileBig5 预设（在 DWG 内容读入前），
    /// 不得依赖 DWGCODEPAGE sysvar（该值已被 session locale 污染）。
    /// </para>
    /// </summary>
    public static void SetCurrentDrawingIsBig5(bool isBig5)
    {
        _currentDrawingIsBig5 = isBig5;
        System.Diagnostics.Debug.WriteLine($"[CodePageHook] SetCurrentDrawingIsBig5: {isBig5}");
        DiagnosticLogger.Log("CodePageHook", $"SetCurrentDrawingIsBig5: {isBig5}");
    }

    /// <summary>
    /// 尝试从 DWG 文件的二进制头部读取 code page 字段，判断是否为 Big5（CP950）图纸。
    /// <para>
    /// 在 DocumentCreateStarted 回调中调用，此时文档对象尚未创建，只有文件路径可用。
    /// R2004+(AC1018+) 读取 header[0x13] 的 AutoCAD 内部 code_page_id。
    /// R12～R2000 读取 header[0x1D5] 的 Windows code page 编号。
    /// </para>
    /// </summary>
    public static bool TryIsDwgFileBig5(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            // 仅处理本地驱动器路径
            if (filePath.Length < 4 || !char.IsLetter(filePath[0]) || filePath[1] != ':'
                || (filePath[2] != '\\' && filePath[2] != '/'))
            {
                DiagnosticLogger.Log("CodePageHook", $"TryIsDwgFileBig5: 跳过非本地路径 '{System.IO.Path.GetFileName(filePath)}'");
                return false;
            }

            if (!System.IO.File.Exists(filePath)) return false;

            byte[] header = new byte[0x1D7];
            using var fs = new System.IO.FileStream(filePath,
                System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            int read = fs.Read(header, 0, header.Length);
            if (read < 8) return false;

            string ver = System.Text.Encoding.ASCII.GetString(header, 0, 6);
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] TryIsDwgFileBig5: ver={ver} file={System.IO.Path.GetFileName(filePath)}");
            DiagnosticLogger.Log("CodePageHook", $"TryIsDwgFileBig5: ver={ver} file={System.IO.Path.GetFileName(filePath)}");

            if (string.Compare(ver, "AC1018", StringComparison.Ordinal) >= 0)
            {
                // R2004+(AC1018+): header[0x13] = AutoCAD 内部 code_page_id
                if (read < 0x14) return false;
                int internalId = header[0x13];
                System.Diagnostics.Debug.WriteLine($"[CodePageHook] TryIsDwgFileBig5: R2004+ code_page_id=0x{internalId:X} (Big5=0x{CodePageIdBig5:X})");
                DiagnosticLogger.Log("CodePageHook", $"TryIsDwgFileBig5: R2004+ code_page_id=0x{internalId:X} (Big5=0x{CodePageIdBig5:X})");
                return internalId == CodePageIdBig5;
            }

            // R12～R2000: header[0x1D5] = Windows code page 编号
            if (read < 0x1D7) return false;
            int codePage = header[0x1D5] | (header[0x1D6] << 8);
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] TryIsDwgFileBig5: R2000- code page={codePage}");
            DiagnosticLogger.Log("CodePageHook", $"TryIsDwgFileBig5: R2000- code page={codePage}");
            return codePage == 950;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError("CodePageHook: TryIsDwgFileBig5 读取失败", ex);
            return false;
        }
    }

    // ─── Hook 安装 ─────────────────────────────────────────────────────────

    private static void InstallHook(IntPtr module)
    {
        // 尝试所有候选 RVA,优先使用通用路径
        foreach (uint rva in CandidateRvas)
        {
            try
            {
                IntPtr targetAddress = module + (int)rva;
                System.Diagnostics.Debug.WriteLine($"[CodePageHook] 尝试 RVA=0x{rva:X}, 地址=0x{targetAddress.ToInt64():X}");
                DiagnosticLogger.Log("CodePageHook", $"尝试 RVA=0x{rva:X}");

                string entryBytesLog = TryReadBytes(targetAddress, 32);
                System.Diagnostics.Debug.WriteLine($"[CodePageHook] 入口字节: {entryBytesLog}");
                DiagnosticLogger.Log("CodePageHook", $"RVA=0x{rva:X} 入口字节: {entryBytesLog}");

                int scannedPrologue = ScanPrologueSize(targetAddress, 14, 64);
                System.Diagnostics.Debug.WriteLine($"[CodePageHook] 序言大小: {scannedPrologue}");
                DiagnosticLogger.Log("CodePageHook", $"RVA=0x{rva:X} 序言大小: {scannedPrologue}");

                if (scannedPrologue < 14)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodePageHook] RVA=0x{rva:X} 序言扫描失败,尝试下一个");
                    DiagnosticLogger.Log("CodePageHook", $"RVA=0x{rva:X} 序言扫描失败（size={scannedPrologue}），尝试下一个。");
                    continue;
                }

                // 找到有效的 RVA,继续安装
                if (TryInstallSingleHook(targetAddress, rva, scannedPrologue))
                {
                    System.Diagnostics.Debug.WriteLine($"[CodePageHook] 成功安装在 RVA=0x{rva:X}");
                    DiagnosticLogger.Log("CodePageHook", $"成功安装在 RVA=0x{rva:X}");
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CodePageHook] RVA=0x{rva:X} 安装失败: {ex.Message}");
                DiagnosticLogger.LogError($"CodePageHook: RVA=0x{rva:X} 安装失败", ex);
                continue;
            }
        }

        // 所有预定义候选都失败,尝试从0x6CEED4向前扫描
        System.Diagnostics.Debug.WriteLine("[CodePageHook] 所有预定义 RVA 失败,尝试向前扫描");
        DiagnosticLogger.Log("CodePageHook", "所有预定义 RVA 失败,尝试从 0x6CEED4 向前扫描");

        TryScanBackwardsForPrologue(module, 0x6CEED4, 0x100);
    }

    private static void TryScanBackwardsForPrologue(IntPtr module, uint suspectedRva, uint scanRange)
    {
        try
        {
            for (uint offset = 0; offset < scanRange; offset += 16)  // x64 函数通常16字节对齐
            {
                if (suspectedRva < offset) break;

                uint testRva = suspectedRva - offset;
                IntPtr testAddr = module + (int)testRva;

                string bytesLog = TryReadBytes(testAddr, 8);
                byte b0 = Marshal.ReadByte(testAddr);
                byte b1 = Marshal.ReadByte(testAddr + 1);

                // 检查是否是典型的函数 prologue
                bool looksLikePrologue = false;
                if (b0 >= 0x40 && b0 <= 0x4F && b1 == 0x89)  // REX mov
                {
                    looksLikePrologue = true;
                }
                else if (b0 >= 0x40 && b0 <= 0x4F && b1 == 0x83)  // REX sub rsp
                {
                    looksLikePrologue = true;
                }
                else if (b0 >= 0x50 && b0 <= 0x57)  // push reg
                {
                    looksLikePrologue = true;
                }

                if (looksLikePrologue)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodePageHook] 发现可能的 prologue at 0x{testRva:X}: {bytesLog}");
                    DiagnosticLogger.Log("CodePageHook", $"扫描发现可能的入口 RVA=0x{testRva:X}: {bytesLog}");

                    int prologueSize = ScanPrologueSize(testAddr, 14, 64);
                    if (prologueSize >= 14)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodePageHook] 尝试安装扫描发现的 RVA=0x{testRva:X}");
                        if (TryInstallSingleHook(testAddr, testRva, prologueSize))
                        {
                            System.Diagnostics.Debug.WriteLine($"[CodePageHook] 成功安装扫描发现的 RVA=0x{testRva:X}");
                            DiagnosticLogger.Log("CodePageHook", $"成功安装扫描发现的 RVA=0x{testRva:X}");
                            return;
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine("[CodePageHook] 向前扫描未找到有效入口");
            DiagnosticLogger.Log("CodePageHook", "向前扫描未找到有效入口");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] 扫描异常: {ex.Message}");
            DiagnosticLogger.LogError("CodePageHook: 扫描异常", ex);
        }
    }

    private static bool TryInstallSingleHook(IntPtr targetAddress, uint rva, int prologueSize)
    {
        try
        {

            _prologueSize = prologueSize;
            _targetAddr = targetAddress;
            _savedBytes = new byte[_prologueSize];
            Marshal.Copy(_targetAddr, _savedBytes, 0, _prologueSize);

            _trampolineAddr = VirtualAlloc(IntPtr.Zero, (uint)(_prologueSize + 14), 0x3000, 0x40);
            if (_trampolineAddr == IntPtr.Zero)
            {
                DiagnosticLogger.Log("CodePageHook", "VirtualAlloc trampoline 失败。");
                return false;
            }

            Marshal.Copy(_savedBytes, 0, _trampolineAddr, _prologueSize);
            WriteAbsoluteJump(_trampolineAddr + _prologueSize, _targetAddr + _prologueSize);
            _trampolineDelegate = Marshal.GetDelegateForFunctionPointer<CodePageFamilyFixDelegate>(_trampolineAddr);

            _hookDelegate = HookHandler;
            IntPtr hookFuncPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);
            _hookStubAddr = CreateReturnAddressCaptureStub(hookFuncPtr, out _returnAddressStorage);

            VirtualProtect(_targetAddr, (uint)_prologueSize, 0x40, out uint oldProtect);
            byte[] patch = new byte[_prologueSize];
            for (int i = 0; i < patch.Length; i++) patch[i] = 0x90;
            WriteAbsoluteJumpBytes(patch, 0, _hookStubAddr);
            Marshal.Copy(patch, 0, _targetAddr, _prologueSize);
            VirtualProtect(_targetAddr, (uint)_prologueSize, oldProtect, out _);

            _installed = true;
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] Hook 安装成功! RVA=0x{rva:X}, PrologueSize={_prologueSize}");
            DiagnosticLogger.Log("CodePageHook", $"Hook 安装成功。RVA=0x{rva:X}, PrologueSize={_prologueSize}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] RVA=0x{rva:X} 安装异常: {ex.Message}");
            DiagnosticLogger.LogError($"CodePageHook: RVA=0x{rva:X} 安装异常", ex);

            if (_trampolineAddr != IntPtr.Zero)
            {
                try { VirtualFree(_trampolineAddr, 0, 0x8000); } catch { }
            }

            _targetAddr = IntPtr.Zero;
            _trampolineAddr = IntPtr.Zero;
            _prologueSize = 0;
            FreeHookStub(ref _hookStubAddr, ref _returnAddressStorage);
            _trampolineDelegate = null;
            _hookDelegate = null;
            _savedBytes = null;

            return false;
        }
    }

    /// <summary>
    /// Hook 处理器。
    /// <para>
    /// 先通过 trampoline 执行原始函数（使 GBK family 写入字段），
    /// 然后检查当前图纸是否为 Big5 图纸。若是且字段被写为 GBK(0x28)，覆写为 Big5(0x27)。
    /// </para>
    /// </summary>
    private static void HookHandler(IntPtr textObject, int arg2)
    {
        if (_inHook)
        {
            _trampolineDelegate?.Invoke(textObject, arg2);
            return;
        }

        _inHook = true;
        try
        {
            Interlocked.Increment(ref _hitCount);
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] HookHandler 触发! HitCount={_hitCount}, IsBig5={_currentDrawingIsBig5}, textObject=0x{textObject.ToInt64():X}");

            // 先执行原始函数
            _trampolineDelegate?.Invoke(textObject, arg2);

            if (textObject == IntPtr.Zero || !IsCommittedMemory(textObject)) return;

            IntPtr fieldAddr = textObject + CodePageIdFieldOffset;
            if (!IsCommittedMemory(fieldAddr)) return;

            int currentCodePageId = Marshal.ReadInt32(fieldAddr);
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] 读取到 CodePageId=0x{currentCodePageId:X} (GBK=0x{CodePageIdGbk:X}, Big5=0x{CodePageIdBig5:X})");
            if (currentCodePageId != CodePageIdGbk) return;

            if (!_currentDrawingIsBig5) return;

            Marshal.WriteInt32(fieldAddr, CodePageIdBig5);
            Interlocked.Increment(ref _patchedCount);
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] 已修正! [0x{textObject.ToInt64():X}+0x{CodePageIdFieldOffset:X}] GBK(0x{CodePageIdGbk:X}) → Big5(0x{CodePageIdBig5:X}), PatchedCount={_patchedCount}");
            DiagnosticLogger.Log("CodePageHook",
                $"已修正: [0x{textObject.ToInt64():X}+0x{CodePageIdFieldOffset:X}] GBK(0x{CodePageIdGbk:X}) → Big5(0x{CodePageIdBig5:X})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodePageHook] HookHandler 异常: {ex.Message}");
            DiagnosticLogger.LogError("CodePageHook: HookHandler 异常", ex);
        }
        finally
        {
            _inHook = false;
        }
    }

    // ─── 辅助方法 ─────────────────────────────────────────────────────────

    private static bool IsCommittedMemory(IntPtr address)
    {
        try
        {
            return VirtualQuery(address, out MemoryBasicInformation info, (uint)Marshal.SizeOf<MemoryBasicInformation>()) != IntPtr.Zero
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
            byte b4 = Marshal.ReadByte(addr + 4);

            // NOP
            if (b0 == 0x90) return 1;

            // push/pop reg
            if (b0 >= 0x50 && b0 <= 0x57) return 1;
            if (b0 >= 0x58 && b0 <= 0x5F) return 1;
            if (b0 == 0x41 && b1 >= 0x50 && b1 <= 0x57) return 2;

            // jmp/call
            if (b0 == 0xFF && b1 == 0x25) return 6;
            if (b0 == 0xE8) return 5; // call rel32
            if (b0 == 0xE9) return 5; // jmp rel32

            // push imm
            if (b0 == 0x6A) return 2;
            if (b0 == 0x68) return 5;

            // xor/test reg,reg
            if (b0 == 0x33 && (b1 >> 6) == 3) return 2;
            if (b0 == 0x85 && (b1 >> 6) == 3) return 2;

            // mov reg, imm32
            if (b0 >= 0xB8 && b0 <= 0xBF) return 5;

            // ret
            if (b0 == 0xC3) return 1;
            if (b0 == 0xC2) return 3;

            // REX prefix (0x40-0x4F) instructions
            if (b0 >= 0x40 && b0 <= 0x4F)
            {
                // REX push/pop
                if (b1 >= 0x50 && b1 <= 0x57) return 2;

                // REX mov reg64, imm64
                if ((b0 == 0x48 || b0 == 0x49) && b1 >= 0xB8 && b1 <= 0xBF) return 10;

                // REX movabs
                if (b0 == 0x48 && (b1 == 0xA1 || b1 == 0xA3)) return 10;

                // REX sub rsp, imm8/imm32
                if (b0 == 0x48 && b1 == 0x83 && b2 == 0xEC) return 4;
                if (b0 == 0x48 && b1 == 0x81 && b2 == 0xEC) return 7;

                // REX add rsp, imm8/imm32
                if (b0 == 0x48 && b1 == 0x83 && b2 == 0xC4) return 4;
                if (b0 == 0x48 && b1 == 0x81 && b2 == 0xC4) return 7;

                // REX mov [rsp+disp], reg
                if (b1 == 0x89 && b2 >= 0x40 && b2 <= 0x7F && b3 == 0x24) return 5;
                if (b1 == 0x89 && b2 >= 0x80 && b2 <= 0xBF && b3 == 0x24) return 8;

                // REX mov reg, [rsp+disp]
                if (b1 == 0x8B && b2 >= 0x40 && b2 <= 0x7F && b3 == 0x24) return 5;
                if (b1 == 0x8B && b2 >= 0x80 && b2 <= 0xBF && b3 == 0x24) return 8;

                // REX mov reg, reg
                if (b0 == 0x4C && b1 == 0x8B && b2 == 0xDC) return 3;
                if ((b0 == 0x4C || b0 == 0x4D || b0 == 0x48 || b0 == 0x49) && b1 == 0x8B && (b2 >> 6) == 3) return 3;
                if ((b0 == 0x48 || b0 == 0x49 || b0 == 0x4C || b0 == 0x4D) && b1 == 0x89 && (b2 >> 6) == 3) return 3;

                // REX lea
                if (b0 == 0x48 && b1 == 0x8D && b2 >= 0x40 && b2 <= 0x7F && b3 == 0x24) return 5;
                if (b0 == 0x48 && b1 == 0x8D && b2 >= 0x80 && b2 <= 0xBF && b3 == 0x24) return 8;
                if ((b0 == 0x48 || b0 == 0x4C) && b1 == 0x8D && (b2 >> 6) == 0 && (b2 & 0x07) == 5) return 7; // lea reg, [rip+disp32]

                // REX xor reg, reg
                if ((b0 == 0x48 || b0 == 0x49) && b1 == 0x33 && (b2 >> 6) == 3) return 3;

                // REX test reg, reg
                if (b0 == 0x48 && b1 == 0x85 && (b2 >> 6) == 3) return 3;

                // REX mov reg, imm32
                if (b0 == 0x48 && b1 == 0xC7 && (b2 & 0xF8) == 0xC0) return 7;

                // REX cmp
                if (b0 == 0x48 && b1 == 0x3B && (b2 >> 6) == 3) return 3;
                if (b0 == 0x48 && b1 == 0x83 && (b2 >> 3 & 0x07) == 7) return 4; // cmp reg, imm8
            }

            return 0;
        }
        catch { return 0; }
    }

    private static IntPtr CreateReturnAddressCaptureStub(IntPtr target, out IntPtr returnAddressStorage)
    {
        returnAddressStorage = VirtualAlloc(IntPtr.Zero, (uint)IntPtr.Size, 0x3000, 0x40);
        if (returnAddressStorage == IntPtr.Zero)
            throw new InvalidOperationException("无法分配返回地址存储内存。");

        IntPtr stub = VirtualAlloc(IntPtr.Zero, 64, 0x3000, 0x40);
        if (stub == IntPtr.Zero)
        {
            VirtualFree(returnAddressStorage, 0, 0x8000);
            returnAddressStorage = IntPtr.Zero;
            throw new InvalidOperationException("无法分配 Hook Stub 内存。");
        }

        byte[] code =
        [
            0x50,                                                       // push rax
            0x48, 0x8B, 0x44, 0x24, 0x08,                               // mov rax, [rsp+8]
            0x48, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0,                         // mov [storage], rax
            0x58,                                                       // pop rax
            0xFF, 0x25, 0, 0, 0, 0,                                     // jmp qword ptr [rip]
            0, 0, 0, 0, 0, 0, 0, 0                                      // target
        ];
        BitConverter.GetBytes(returnAddressStorage.ToInt64()).CopyTo(code, 8);
        BitConverter.GetBytes(target.ToInt64()).CopyTo(code, 23);
        Marshal.Copy(code, 0, stub, code.Length);
        return stub;
    }

    private static void FreeHookStub(ref IntPtr stub, ref IntPtr returnAddressStorage)
    {
        if (stub != IntPtr.Zero)
        {
            try { VirtualFree(stub, 0, 0x8000); } catch { }
            stub = IntPtr.Zero;
        }
        if (returnAddressStorage != IntPtr.Zero)
        {
            try { VirtualFree(returnAddressStorage, 0, 0x8000); } catch { }
            returnAddressStorage = IntPtr.Zero;
        }
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
        if (address == IntPtr.Zero) return string.Empty;
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

    // ─── P/Invoke ─────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, uint dwLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFree(IntPtr lpAddress, uint dwSize, uint dwFreeType);

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
#endif
