using System.Runtime.InteropServices;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DBCS Code Page Family 证据 Hook。
/// <para>
/// 通过运行时唯一签名定位 code-page context 初始化函数，只读取 AutoCAD 初始化后的
/// <c>[context+0x46C]</c> 与当前 DWG filer code page 是否不一致，不再写回 native 字段。
/// </para>
/// </summary>
internal static class CodePageFamilyHook
{
    private const string Tag = "CodePageHook";
    private const int CodePageIdFieldOffset = 0x46C;
    private const uint MemCommit = 0x1000;
    private const int NoScopeLogLimit = 80;

    private static readonly byte[] TargetPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x08,
        0x48, 0x89, 0x6C, 0x24, 0x10,
        0x48, 0x89, 0x74, 0x24, 0x18,
        0x57,
        0x48, 0x83, 0xEC, 0x20
    ];

    private static readonly int[] TargetSignature =
    [
        0x48, 0x89, 0x5C, 0x24, 0x08,
        0x48, 0x89, 0x6C, 0x24, 0x10,
        0x48, 0x89, 0x74, 0x24, 0x18,
        0x57,
        0x48, 0x83, 0xEC, 0x20,
        0x33, 0xED,
        0x89, 0x11,
        0x48, 0xB8,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F,
        0x66, 0x89, 0x69, 0x04,
        0x83, 0xCE, 0xFF,
        0x48, 0x89, 0x41, 0x08,
        0x89, 0x71, 0x10,
        0x48, 0x8B, 0xF9,
        0x89, 0x69, 0x14,
        0x89, 0x71, 0x18,
        0x48, 0x83, 0xC1, 0x20
    ];

    private static NativeInlineHook<CodePageFamilyInitDelegate>? _hook;
    private static IntPtr _moduleBase;
    private static bool _installed;
    private static int _hitCount;
    private static int _mismatchEvidenceCount;
    private static int _noScopeCount;
    private static int _sameCodePageCount;
    private static int _invalidMemoryCount;
    private static int _noScopeLogCount;
    private static int _lastOriginalCodePageId;
    private static int _lastFilerCodePageId;
    private static int _lastEvidenceCodePageId;
    private static uint _lastReturnRva;
    [ThreadStatic] private static bool _inHook;

    /// <summary>
    /// code-page family 初始化函数委托。
    /// </summary>
    /// <param name="context">文本解码上下文指针。</param>
    /// <param name="arg2">原始第二参数。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CodePageFamilyInitDelegate(IntPtr context, int arg2);

    /// <summary>
    /// 安装 Code Page Family 修复 Hook。
    /// <para>在插件初始化阶段调用。</para>
    /// </summary>
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

        if (!NativeModulePatternScanner.TryFindUniqueTextPattern(
                _moduleBase,
                TargetSignature,
                Tag,
                "TextEditor code-page context init",
                out IntPtr targetAddress,
                out uint targetRva))
            return;

        _hook = new NativeInlineHook<CodePageFamilyInitDelegate>(
            Tag,
            "TextEditor code-page context init",
            targetRva);
        _installed = _hook.InstallAtAddress(targetAddress, targetRva, HookHandler, TargetPrefix.Length, 64, TargetPrefix);
    }

    /// <summary>卸载 Hook。</summary>
    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook = null;
        _moduleBase = IntPtr.Zero;
        _installed = false;
        DiagnosticLogger.Log(Tag, $"已卸载。HitCount={_hitCount}, MismatchEvidence={_mismatchEvidenceCount}, NoScope={_noScopeCount}");
    }

    /// <summary>获取诊断报告。</summary>
    public static string GetReport()
    {
        return string.Join(Environment.NewLine,
            "=== Code Page Family Hook ===",
            $"Installed: {_installed}",
            $"HitCount: {_hitCount}",
            $"MismatchEvidenceCount: {_mismatchEvidenceCount}",
            $"NoDbcsScopeCount: {_noScopeCount}",
            $"SameCodePageCount: {_sameCodePageCount}",
            $"InvalidMemoryCount: {_invalidMemoryCount}",
            $"LastReturnRva: 0x{_lastReturnRva:X}",
            $"LastOriginalCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastOriginalCodePageId)}",
            $"LastFilerCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastFilerCodePageId)}",
            $"LastEvidenceCodePageId: {DwgFilerCodePageScopeHook.FormatCodePageId(_lastEvidenceCodePageId)}");
    }

    private static bool IsSupportedPlatform()
    {
        if (!PlatformManager.Platform.AcDbDllName.Equals("acdb25.dll", StringComparison.OrdinalIgnoreCase)
            || !PlatformManager.Platform.VersionName.Equals("2025", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticLogger.Log(Tag,
                $"{PlatformManager.Platform.DisplayName} 未验证 code-page context 签名，跳过安装。");
            return false;
        }

        return true;
    }

    private static void HookHandler(IntPtr context, int arg2)
    {
        var trampoline = _hook?.TrampolineDelegate;
        if (trampoline == null)
            return;

        if (_inHook)
        {
            trampoline(context, arg2);
            return;
        }

        _inHook = true;
        try
        {
            Interlocked.Increment(ref _hitCount);
            _lastReturnRva = GetReturnRva(_hook?.CapturedReturnAddress ?? IntPtr.Zero);

            trampoline(context, arg2);

            if (context == IntPtr.Zero || !IsCommittedMemory(context))
            {
                Interlocked.Increment(ref _invalidMemoryCount);
                return;
            }

            IntPtr fieldAddr = context + CodePageIdFieldOffset;
            if (!IsCommittedMemory(fieldAddr))
            {
                Interlocked.Increment(ref _invalidMemoryCount);
                return;
            }

            int originalCodePageId = Marshal.ReadInt32(fieldAddr);
            _lastOriginalCodePageId = originalCodePageId;

            if (!DwgFilerCodePageScopeHook.TryGetCurrentDbcsCodePageId(out int filerCodePageId))
            {
                Interlocked.Increment(ref _noScopeCount);
                if (Interlocked.Increment(ref _noScopeLogCount) <= NoScopeLogLimit)
                {
                    DiagnosticLogger.Log(Tag,
                        $"无证据: return=0x{_lastReturnRva:X}, context=0x{context.ToInt64():X}, " +
                        $"original={DwgFilerCodePageScopeHook.FormatCodePageId(originalCodePageId)}, no readString DBCS scope");
                }
                return;
            }

            _lastFilerCodePageId = filerCodePageId;
            if (originalCodePageId == filerCodePageId)
            {
                Interlocked.Increment(ref _sameCodePageCount);
                return;
            }

            _lastEvidenceCodePageId = filerCodePageId;
            Interlocked.Increment(ref _mismatchEvidenceCount);
            DbTextDwgInFieldsScopeHook.RecordCodePageFamilyEvidence(originalCodePageId, filerCodePageId);

            DiagnosticLogger.Log(Tag,
                $"证据命中: return=0x{_lastReturnRva:X}, context=0x{context.ToInt64():X}, " +
                $"{DwgFilerCodePageScopeHook.FormatCodePageId(originalCodePageId)} -> " +
                $"{DwgFilerCodePageScopeHook.FormatCodePageId(filerCodePageId)}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": HookHandler 异常", ex);
        }
        finally
        {
            _inHook = false;
        }
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
