#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// DBText Hook 追踪器 - 运行时监控 code-page family 字段写入。
/// <para>
/// 通过在多个候选 RVA 安装轻量级计数 Hook，记录每个地址的命中次数，
/// 帮助识别 DBText 反序列化时实际使用的 code-page 写入路径。
/// </para>
/// </summary>
internal static class DbTextHookTracer
{
    private static bool _installed = false;
    private static IntPtr _moduleBase;
    private static readonly Dictionary<uint, ProbeInfo> _probes = new();
    private static readonly object _lock = new();

    private class ProbeInfo
    {
        public uint Rva { get; set; }
        public IntPtr Address { get; set; }
        public int HitCount { get; set; }
        public byte[] SavedBytes { get; set; } = Array.Empty<byte>();
        public IntPtr TrampolineAddr { get; set; }
        public IntPtr HookStubAddr { get; set; }
        public Action<IntPtr, int>? TrampolineDelegate { get; set; }
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// 安装追踪 Hook 到多个候选 RVA。
    /// </summary>
    public static void Install()
    {
        if (_installed) return;

        lock (_lock)
        {
            if (_installed) return;

            string dllName = PlatformManager.Platform.AcDbDllName;
            if (!dllName.Equals("acdb25.dll", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLogger.Log("DbTextTracer", $"{dllName} 不支持追踪。");
                return;
            }

            _moduleBase = GetModuleHandle(dllName);
            if (_moduleBase == IntPtr.Zero)
            {
                DiagnosticLogger.Log("DbTextTracer", $"{dllName} 未加载。");
                return;
            }

            // 候选 RVA - 基于记忆文件中的调查结果
            var candidates = new (uint Rva, string Description)[]
            {
                // 核心目标
                (0x6CEED4, "core: code-page family writer (suspected)"),
                (0x6AC1A4, "core: code-page family reader (cache loader)"),

                // MText 路径 (已确认)
                (0x6CFE6C, "MText: known working RVA"),
                (0x88C4F8, "MText: AcDbImpMText slot-15 entry"),
                (0x6D0BB4, "MText: thin wrapper -> 0x6D0BFC"),
                (0x6D0BFC, "MText: double fork dispatcher"),
                (0x6D0CA8, "MText: large dispatcher"),

                // 共享路径
                (0x6D2388, "shared: init orchestrator (6 callers)"),
                (0x6D2410, "shared: 0x6D2388 symmetric sibling (4 callers)"),
                (0x6D2498, "shared: helper (11 callers)"),
                (0x6CB79C, "shared: orchestration node"),
                (0x6D230C, "shared: DBCS bridge (calls 0x2E4D0C+0x131360)"),
                (0x6D2574, "shared: 0x6D230C symmetric sibling"),

                // 候选扩展
                (0x6D1D34, "candidate: alternate dispatcher (vs 0x6D0BB4)"),
                (0x6D1D7C, "candidate: alternate fork (vs 0x6D0BFC)"),
                (0x8F02E4, "candidate: third branch entry"),
                (0x6D2654, "candidate: char content dispatcher"),
                (0x6CE334, "candidate: byte-char reader"),
                (0x6CE3BC, "candidate: word-char reader"),

                // 向前扫描区域 (0x6CEED4 前后)
                (0x6CEEC0, "scan: -0x14 from 0x6CEED4"),
                (0x6CEEB0, "scan: -0x24"),
                (0x6CEEA0, "scan: -0x34"),
                (0x6CEE90, "scan: -0x44"),
                (0x6CEE80, "scan: -0x54"),
                (0x6CEE70, "scan: -0x64"),
            };

            DiagnosticLogger.BeginPhase("安装 DBText Hook 追踪器");
            int successCount = 0;

            foreach (var (rva, description) in candidates)
            {
                try
                {
                    IntPtr addr = _moduleBase + (int)rva;
                    if (!IsCommittedMemory(addr))
                    {
                        DiagnosticLogger.Log("DbTextTracer", $"RVA 0x{rva:X}: 内存无效");
                        continue;
                    }

                    // 仅记录，不实际安装 Hook (避免破坏程序稳定性)
                    var probe = new ProbeInfo
                    {
                        Rva = rva,
                        Address = addr,
                        HitCount = 0,
                        Description = description
                    };

                    _probes[rva] = probe;
                    successCount++;

                    DiagnosticLogger.Log("DbTextTracer", $"注册监控: RVA=0x{rva:X} ({description})");
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Log("DbTextTracer", $"RVA 0x{rva:X} 注册失败: {ex.Message}");
                }
            }

            _installed = true;
            DiagnosticLogger.EndPhase($"已注册 {successCount}/{candidates.Length} 个监控点");
            DiagnosticLogger.Log("DbTextTracer", $"追踪器已安装。注意：当前实现为被动监控，需要结合调试器断点使用。");
        }
    }

    /// <summary>
    /// 卸载追踪 Hook。
    /// </summary>
    public static void Uninstall()
    {
        if (!_installed) return;

        lock (_lock)
        {
            if (!_installed) return;

            foreach (var probe in _probes.Values)
            {
                try
                {
                    if (probe.TrampolineAddr != IntPtr.Zero)
                        VirtualFree(probe.TrampolineAddr, 0, 0x8000);
                    if (probe.HookStubAddr != IntPtr.Zero)
                        VirtualFree(probe.HookStubAddr, 0, 0x8000);
                }
                catch { }
            }

            _probes.Clear();
            _installed = false;
            DiagnosticLogger.Log("DbTextTracer", "追踪器已卸载。");
        }
    }

    /// <summary>
    /// 获取追踪报告 - 列出所有监控点及其命中计数。
    /// </summary>
    public static string GetReport()
    {
        lock (_lock)
        {
            if (!_installed || _probes.Count == 0)
                return "追踪器未安装或无监控点。";

            var lines = new List<string>
            {
                "=== DBText Hook 追踪报告 ===",
                $"模块基址: 0x{_moduleBase.ToInt64():X}",
                $"监控点数量: {_probes.Count}",
                ""
            };

            // 按命中次数降序排序
            var sorted = _probes.Values.OrderByDescending(p => p.HitCount).ToList();

            lines.Add("命中统计:");
            foreach (var probe in sorted)
            {
                string status = probe.HitCount > 0 ? "★" : " ";
                lines.Add($"  {status} RVA 0x{probe.Rva:X}: {probe.HitCount,5} 次 - {probe.Description}");
            }

            lines.Add("");
            lines.Add("说明:");
            lines.Add("  ★ = 已命中 (可能是 DBText 路径)");
            lines.Add("    = 未命中 (非 DBText 路径或未触发)");

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// 手动增加指定 RVA 的命中计数 (供调试器脚本调用)。
    /// </summary>
    public static void RecordHit(uint rva)
    {
        lock (_lock)
        {
            if (_probes.TryGetValue(rva, out var probe))
            {
                probe.HitCount++;
                System.Diagnostics.Debug.WriteLine($"[DbTextTracer] RVA 0x{rva:X} 命中: {probe.HitCount} 次");
            }
        }
    }

    // ─── 辅助方法 ─────────────────────────────────────────────────────────

    private static bool IsCommittedMemory(IntPtr address)
    {
        try
        {
            return VirtualQuery(address, out MemoryBasicInformation info, (uint)Marshal.SizeOf<MemoryBasicInformation>()) != IntPtr.Zero
                && info.State == 0x1000; // MEM_COMMIT
        }
        catch { return false; }
    }

    // ─── P/Invoke ─────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, uint dwLength);

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
