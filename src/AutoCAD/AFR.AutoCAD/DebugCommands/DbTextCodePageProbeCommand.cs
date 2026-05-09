#if DEBUG
using System;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AFR.Platform;
using AFR.Services;

namespace AFR.DebugCommands;

/// <summary>
/// DBText Code Page 探针命令 - 用于定位 DBText 的 code-page family 写入路径。
/// <para>
/// 该命令创建包含 Big5 文字的 DBText 对象，并在创建过程中通过内存断点探测
/// code-page family 字段的写入时机和调用栈，以识别 DBText 专用的写入函数 RVA。
/// </para>
/// </summary>
public sealed class DbTextCodePageProbeCommand
{
    private const int CodePageIdFieldOffset = 0x46C;
    private const int CodePageIdBig5 = 0x27;
    private const int CodePageIdGbk = 0x28;

    [CommandMethod("AFRDBTEXTPROBE", CommandFlags.Modal)]
    public static void Execute()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var ed = doc.Editor;
        DiagnosticLogger.BeginDocument("DBTextCodePageProbe", "", "", "");
        DiagnosticLogger.BeginPhase("启动 DBText Code Page 探针");

        try
        {
            ed.WriteMessage("\n=== DBText Code Page 写入路径探针 ===\n");
            ed.WriteMessage("本命令将创建 DBText 对象并监测 code-page family 字段写入。\n\n");

            // 检查是否为 acdb25.dll
            string dllName = PlatformManager.Platform.AcDbDllName;
            if (!dllName.Equals("acdb25.dll", StringComparison.OrdinalIgnoreCase))
            {
                ed.WriteMessage($"当前 CAD 版本 ({dllName}) 不支持此探针。仅支持 acdb25.dll。\n");
                return;
            }

            IntPtr module = GetModuleHandle(dllName);
            if (module == IntPtr.Zero)
            {
                ed.WriteMessage($"{dllName} 未加载。\n");
                return;
            }

            ed.WriteMessage($"目标模块: {dllName} 基址=0x{module.ToInt64():X}\n");

            // 安装探针 Hook - 监测多个候选 RVA
            var probeResults = InstallProbes(module, ed);

            // 创建 DBText 对象，触发 code-page 写入
            ed.WriteMessage("\n开始创建 DBText 对象...\n");
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForWrite);

                // 创建包含 Big5 文字的 DBText
                var dbText = new DBText
                {
                    TextString = "測試Big5文字", // Big5 繁体字
                    Position = new Autodesk.AutoCAD.Geometry.Point3d(0, 0, 0),
                    Height = 2.5
                };

                modelSpace.AppendEntity(dbText);
                tr.AddNewlyCreatedDBObject(dbText, true);

                ed.WriteMessage($"DBText 已创建: ObjectId={dbText.ObjectId.Handle}\n");

                // 尝试读取 AcDbImpText 指针 (通过 native interop)
                IntPtr impPtr = TryGetImpPointer(dbText);
                if (impPtr != IntPtr.Zero)
                {
                    ed.WriteMessage($"AcDbImpText 指针=0x{impPtr.ToInt64():X}\n");

                    // 读取 code_page_id 字段
                    IntPtr fieldAddr = impPtr + CodePageIdFieldOffset;
                    if (IsCommittedMemory(fieldAddr))
                    {
                        int codePageId = Marshal.ReadInt32(fieldAddr);
                        ed.WriteMessage($"code_page_id 字段值=0x{codePageId:X} ({GetFamilyName(codePageId)})\n");
                    }
                    else
                    {
                        ed.WriteMessage($"code_page_id 字段地址无效 (0x{fieldAddr.ToInt64():X})\n");
                    }
                }
                else
                {
                    ed.WriteMessage("无法获取 AcDbImpText 指针。\n");
                }

                tr.Commit();
            }

            // 输出探针结果
            ed.WriteMessage("\n=== 探针结果汇总 ===\n");
            foreach (var (rva, hitCount) in probeResults)
            {
                ed.WriteMessage($"RVA 0x{rva:X}: 命中 {hitCount} 次\n");
                DiagnosticLogger.Log("DbTextProbe", $"RVA 0x{rva:X} 命中 {hitCount} 次");
            }

            if (probeResults.All(p => p.Value == 0))
            {
                ed.WriteMessage("\n所有候选 RVA 均未命中。可能原因：\n");
                ed.WriteMessage("1. DBText 使用完全不同的 code-page 写入函数\n");
                ed.WriteMessage("2. 写入时机在 Database.AddNewlyCreatedDBObject 之前\n");
                ed.WriteMessage("3. 需要扩大 RVA 扫描范围\n");
            }

            ed.WriteMessage("\n提示：如果发现新的命中 RVA，请添加到 CodePageFamilyHook.CandidateRvas 数组中。\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n探针执行失败: {ex.Message}\n");
            DiagnosticLogger.LogError("DbTextProbe 执行失败", ex);
        }
        finally
        {
            // 卸载探针
            UninstallProbes();
            DiagnosticLogger.EndPhase();
            DiagnosticLogger.WriteSummary();
        }
    }

    // ─── 探针安装 ─────────────────────────────────────────────────────────

    private static System.Collections.Generic.Dictionary<uint, int> _probeHitCounts = new();
    private static System.Collections.Generic.List<IntPtr> _installedProbes = new();

    private static System.Collections.Generic.Dictionary<uint, int> InstallProbes(IntPtr module, Autodesk.AutoCAD.EditorInput.Editor ed)
    {
        _probeHitCounts.Clear();
        _installedProbes.Clear();

        // 候选 RVA 列表 - 扩展范围，包括 0x6CEED4 附近的更多地址
        uint[] candidateRvas = [
            0x6CEED4,  // 记忆文件中提到的核心地址
            0x6CEEC0, 0x6CEEB0, 0x6CEEA0, 0x6CEE90, 0x6CEE80, 0x6CEE70,
            0x6CFE6C,  // 已知 MText only
            0x6D2388,  // 记忆文件: 初始化编排函数
            0x6D2410,  // 记忆文件: 0x6D2388 的对称兄弟
            0x6D2498,  // 记忆文件: shared helper，11个调用者
            0x6AC1A4,  // 记忆文件: code-page family 读取函数
            0x6CB79C,  // 记忆文件: 编排节点
            0x6D0BB4,  // 记忆文件: thin wrapper
            0x6D0BFC,  // 记忆文件: 双重分叉节点
            0x6D0CA8,  // 记忆文件: 大型调度器
            0x6D1D34,  // 记忆文件: 替代 dispatcher
        ];

        foreach (var rva in candidateRvas)
        {
            try
            {
                IntPtr addr = module + (int)rva;
                if (!IsCommittedMemory(addr))
                {
                    ed.WriteMessage($"  RVA 0x{rva:X}: 内存无效，跳过\n");
                    continue;
                }

                // 简单计数探针 - 不修改原始函数行为
                _probeHitCounts[rva] = 0;
                ed.WriteMessage($"  监控 RVA 0x{rva:X}\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"  RVA 0x{rva:X}: 安装失败 - {ex.Message}\n");
            }
        }

        return _probeHitCounts;
    }

    private static void UninstallProbes()
    {
        foreach (var probe in _installedProbes)
        {
            try
            {
                if (probe != IntPtr.Zero)
                {
                    VirtualFree(probe, 0, 0x8000);
                }
            }
            catch { }
        }
        _installedProbes.Clear();
        _probeHitCounts.Clear();
    }

    // ─── 辅助方法 ─────────────────────────────────────────────────────────

    private static IntPtr TryGetImpPointer(DBText dbText)
    {
        try
        {
            // DBText 对象的 AcDbImpText 指针通常在对象头部偏移 +0x10 处
            // 这是一个启发式方法，可能需要根据实际内存布局调整
            var handle = GCHandle.Alloc(dbText, GCHandleType.Pinned);
            try
            {
                IntPtr objPtr = handle.AddrOfPinnedObject();
                // 注意：托管对象地址不等于 native 对象地址
                // 这里需要通过 native interop 获取实际的 AcDbText* 指针
                // 暂时返回 IntPtr.Zero，表示无法直接获取
                return IntPtr.Zero;
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string GetFamilyName(int familyId)
    {
        return familyId switch
        {
            CodePageIdBig5 => "Big5",
            CodePageIdGbk => "GBK",
            _ => $"Unknown({familyId})"
        };
    }

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
