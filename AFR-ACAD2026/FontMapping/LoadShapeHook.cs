using System.Runtime.InteropServices;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.FontMapping;

/// <summary>
/// x64 inline hook on acdb25!loadShape。
/// 拦截 SHX 字体加载函数，在首次调用时注入字体映射，
/// 并将所有 @前缀 大字体名的 @ 去除（竖排→横排）。
///
/// Hook 架构（使用近跳 + 中继跳板，因为函数前 13 字节安全但不足 14 字节）:
///
///   [原始函数头部]  ──5字节JMP──→  [中继跳板 (±2GB内)]  ──14字节绝对JMP──→  [Detour]
///                                                                              │
///   [Trampoline: 13字节原始指令 + 14字节绝对JMP回原始+13]  ←─── 调用 ──────────┘
/// </summary>
internal static class LoadShapeHook
{
    #region Win32 P/Invoke

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint hProcess, nint lpBaseAddress, nuint dwSize);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;

    #endregion

    // 安全拷贝字节数（6个push + sub rsp,0x78 = 13字节，无RIP相对指令）
    private const int STOLEN_BYTES = 13;

    // 绝对跳转指令长度: FF 25 00 00 00 00 [8字节地址] = 14字节
    private const int ABS_JMP_SIZE = 14;

    // 近跳转指令长度: E9 [4字节偏移] = 5字节
    private const int REL_JMP_SIZE = 5;

    private const string LoadShapeExportName =
        "?loadShape@@YA?AW4ErrorStatus@Acad@@PEB_W0AEAVAcDbObjectId@@AEAHPEAVAcDbDatabase@@_N4@Z";

    /// <summary>
    /// loadShape 函数签名匹配的委托。
    /// 所有参数使用 nint 以匹配 x64 寄存器/栈槽宽度。
    /// 返回值为 Acad::ErrorStatus (int)。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LoadShapeFn(
        nint fontName,     // const wchar_t* (RCX)
        nint bigFontName,  // const wchar_t* (RDX)
        nint objectId,     // AcDbObjectId&  (R8)
        nint shapeNumber,  // int&           (R9)
        nint database,     // AcDbDatabase*  (stack)
        nint param6,       // bool           (stack)
        nint param7);      // bool           (stack)

    // 防止 GC 回收
    private static LoadShapeFn? _detourDelegate;
    private static LoadShapeFn? _trampolineDelegate;
    private static GCHandle _detourPin;

    private static bool _installed;
    private static int _logCount;  // 诊断日志计数

    /// <summary>
    /// 安装 Hook。在 PluginEntry.Initialize() 中调用。
    /// </summary>
    internal static bool Install()
    {
        if (_installed) return true;

        var log = LogService.Instance;

        try
        {
            // 1. 定位 loadShape
            nint acdb = GetModuleHandle("acdb25.dll");
            if (acdb == 0) { log.Warning("LoadShapeHook: acdb25.dll 未加载"); return false; }

            nint target = GetProcAddress(acdb, LoadShapeExportName);
            if (target == 0) { log.Warning("LoadShapeHook: 未找到 loadShape 导出"); return false; }

            // 2. 验证入口字节
            byte[] entryBytes = new byte[STOLEN_BYTES + 8];
            Marshal.Copy(target, entryBytes, 0, entryBytes.Length);
            byte[] expected = [0x40, 0x53, 0x55, 0x56, 0x57, 0x41, 0x56, 0x41, 0x57, 0x48, 0x83, 0xEC, 0x78];
            for (int i = 0; i < expected.Length; i++)
            {
                if (entryBytes[i] != expected[i])
                {
                    string hex = string.Join(" ", entryBytes.Select(b => b.ToString("X2")));
                    log.Warning($"LoadShapeHook: 入口字节不匹配，跳过安装。实际: {hex}");
                    return false;
                }
            }

            // 3. 创建 Detour 委托并获取函数指针
            _detourDelegate = Detour;
            _detourPin = GCHandle.Alloc(_detourDelegate);
            nint detourPtr = Marshal.GetFunctionPointerForDelegate(_detourDelegate);

            // 4. 在 ±2GB 范围内分配中继跳板（用于 5字节近跳 → 14字节绝对跳）
            nint relay = AllocateNearby(target, ABS_JMP_SIZE);
            if (relay == 0) { log.Warning("LoadShapeHook: 无法分配中继跳板"); return false; }

            // 中继跳板: 14字节绝对跳转到 Detour
            WriteAbsoluteJmp(relay, detourPtr);

            // 5. 分配 Trampoline（原始指令 + 跳回）
            nint trampoline = VirtualAlloc(0, (nuint)(STOLEN_BYTES + ABS_JMP_SIZE), MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (trampoline == 0) { log.Warning("LoadShapeHook: 无法分配 Trampoline"); return false; }

            // 拷贝原始 13 字节
            Marshal.Copy(entryBytes, 0, trampoline, STOLEN_BYTES);
            // 绝对跳回 target + STOLEN_BYTES
            WriteAbsoluteJmp(trampoline + STOLEN_BYTES, target + STOLEN_BYTES);

            _trampolineDelegate = Marshal.GetDelegateForFunctionPointer<LoadShapeFn>(trampoline);

            // 6. 修改原始函数：写入 5字节近跳到中继跳板
            if (!VirtualProtect(target, (nuint)STOLEN_BYTES, PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                log.Warning("LoadShapeHook: VirtualProtect 失败");
                return false;
            }

            // E9 [rel32] — 相对跳转到 relay
            long relOffset = relay - (target + REL_JMP_SIZE);
            if (relOffset < int.MinValue || relOffset > int.MaxValue)
            {
                log.Warning("LoadShapeHook: 中继跳板超出 ±2GB 范围");
                VirtualProtect(target, (nuint)STOLEN_BYTES, oldProtect, out _);
                return false;
            }

            Marshal.WriteByte(target, 0xE9);
            Marshal.WriteInt32(target + 1, (int)relOffset);
            // 用 NOP 填充剩余字节 (13 - 5 = 8 字节)
            for (int i = REL_JMP_SIZE; i < STOLEN_BYTES; i++)
                Marshal.WriteByte(target + i, 0x90);

            VirtualProtect(target, (nuint)STOLEN_BYTES, oldProtect, out _);
            FlushInstructionCache(GetCurrentProcess(), target, (nuint)STOLEN_BYTES);

            _installed = true;
            log.Info("LoadShapeHook: 已安装（loadShape 入口 Hook）");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("LoadShapeHook: 安装失败", ex);
            return false;
        }
    }

    /// <summary>
    /// Detour 函数 — 拦截每次 loadShape 调用。
    /// 1. 记录前 10 次调用的参数用于诊断
    /// 2. 若第二参数（大字体名）以 @ 开头，去除 @ 前缀后传入原始函数
    /// </summary>
    private static int Detour(nint fontName, nint bigFontName, nint objectId, nint shapeNumber,
                              nint database, nint param6, nint param7)
    {
        try
        {
            string? font = fontName != 0 ? Marshal.PtrToStringUni(fontName) : null;
            string? bigFont = bigFontName != 0 ? Marshal.PtrToStringUni(bigFontName) : null;

            // 诊断日志：前 10 次调用
            int count = Interlocked.Increment(ref _logCount);
            if (count <= 10)
            {
                LogService.Instance.Info($"LoadShapeHook #{count}: font=\"{font}\", bigFont=\"{bigFont}\"");
            }

            // 核心逻辑：去除大字体名的 @ 前缀（竖排→横排）
            if (bigFont != null && bigFont.StartsWith('@'))
            {
                string fixedBigFont = bigFont[1..];
                nint fixedPtr = Marshal.StringToHGlobalUni(fixedBigFont);
                try
                {
                    if (count <= 10)
                        LogService.Instance.Info($"  → 修正大字体: {bigFont} → {fixedBigFont}");

                    return _trampolineDelegate!(fontName, fixedPtr, objectId, shapeNumber,
                                                database, param6, param7);
                }
                finally
                {
                    Marshal.FreeHGlobal(fixedPtr);
                }
            }
        }
        catch
        {
            // 任何异常都不能阻止原始函数执行
        }

        return _trampolineDelegate!(fontName, bigFontName, objectId, shapeNumber,
                                    database, param6, param7);
    }

    /// <summary>
    /// 在目标地址 ±2GB 范围内分配可执行内存（用于近跳中继）。
    /// </summary>
    private static nint AllocateNearby(nint target, int size)
    {
        const long range = 0x7FFF0000L; // 略小于 2GB，留安全余量
        long baseAddr = target;
        long minAddr = Math.Max(baseAddr - range, 0x10000);
        long maxAddr = baseAddr + range;

        // 从目标地址向低地址搜索可用内存页
        const int pageSize = 0x10000; // 64KB 对齐
        for (long addr = baseAddr - pageSize; addr >= minAddr; addr -= pageSize)
        {
            nint result = VirtualAlloc((nint)addr, (nuint)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (result != 0) return result;
        }

        // 从目标地址向高地址搜索
        for (long addr = baseAddr + pageSize; addr <= maxAddr; addr += pageSize)
        {
            nint result = VirtualAlloc((nint)addr, (nuint)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (result != 0) return result;
        }

        return 0;
    }

    /// <summary>
    /// 写入 14 字节绝对跳转指令: FF 25 00 00 00 00 [8字节目标地址]
    /// </summary>
    private static void WriteAbsoluteJmp(nint writeAddr, nint targetAddr)
    {
        Marshal.WriteByte(writeAddr, 0xFF);
        Marshal.WriteByte(writeAddr + 1, 0x25);
        Marshal.WriteInt32(writeAddr + 2, 0);
        Marshal.WriteInt64(writeAddr + 6, targetAddr);
    }
}
