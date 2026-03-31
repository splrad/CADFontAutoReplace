using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.FontMapping;

/// <summary>
/// 单条 MText 字体映射记录。
/// </summary>
internal sealed record InlineFontFixRecord(
    string MissingFont,
    string ReplacementFont,
    string FixMethod,      // "MText映射"
    string FontCategory);  // "SHX主字体" / "SHX大字体" / "TrueType"

/// <summary>
/// Hook acdb25.dll 的 ldfile 函数，在 DWG 解析阶段拦截缺失字体文件加载。
///
/// 设计原理：
///   DWG 解析阶段: Hook 重定向所有缺失字体（含样式表 + MText 内联字体），
///   确保 MText 内联字体在首次渲染时就能正确显示。
///   Execute 阶段: FontReplacer 覆盖样式表字体，用户可通过 ST/AFRLOG 随时调整。
///   AFRLOG 显示: GetRedirectRecords() 过滤掉样式表字体，仅展示 MText 内联字体映射。
///
/// 样式表字体虽然在 DWG 解析阶段被 Hook 重定向，但 FontReplacer 会用用户配置覆盖，
/// 因此样式表字体始终可控，无需重启 CAD。
/// </summary>
internal static class LdFileHook
{
    private const string AcDbDll = "acdb25.dll";
    private const string LdFileExport = "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z";
    private const int PrologueSize = 21; // 完整指令边界：8×PUSH + LEA

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LdFileDelegate(IntPtr fileName, int param2, IntPtr db, IntPtr desc);

    // Hook 基础设施
    private static LdFileDelegate? _hookDelegate;
    private static LdFileDelegate? _trampolineDelegate;
    private static IntPtr _targetAddr;
    private static IntPtr _trampolineAddr;
    private static byte[]? _savedBytes;
    private static volatile bool _installed;

    // 字体解析状态
    private static readonly HashSet<string> _availableFonts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _bigFontFiles = new(StringComparer.OrdinalIgnoreCase);
    private static string _repMainFont = "";
    private static string _repBigFont = "";
    [ThreadStatic] private static bool _inHook;

    // 记录本次会话的重定向: fontName → (replacement, fontType)
    private static readonly ConcurrentDictionary<string, (string Replacement, int FontType)> _redirectLog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 安装 ldfile Hook。必须在任何文档打开之前调用（PluginEntry.Initialize）。
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;

        var log = LogService.Instance;

        try
        {
            IntPtr module = GetModuleHandle(AcDbDll);
            if (module == IntPtr.Zero) { log.Warning("FontMapping: acdb25.dll 未加载"); return; }

            _targetAddr = GetProcAddress(module, LdFileExport);
            if (_targetAddr == IntPtr.Zero) { log.Warning("FontMapping: 未找到 ldfile 导出"); return; }

            // 加载用户配置
            var config = ConfigService.Instance;
            if (!string.IsNullOrEmpty(config.MainFont))
                _repMainFont = EnsureShx(config.MainFont);
            if (!string.IsNullOrEmpty(config.BigFont))
                _repBigFont = EnsureShx(config.BigFont);

            if (string.IsNullOrEmpty(_repMainFont) && string.IsNullOrEmpty(_repBigFont))
            {
                log.Info("FontMapping: 未配置替换字体，Hook 未安装");
                return;
            }

            // 扫描可用字体
            ScanAvailableFonts();

            // 保存原始字节
            _savedBytes = new byte[PrologueSize];
            Marshal.Copy(_targetAddr, _savedBytes, 0, PrologueSize);

            // 创建 Trampoline：执行保存的原始指令 + JMP 回原函数
            int trampolineSize = PrologueSize + 14; // 14 = absolute JMP
            _trampolineAddr = VirtualAlloc(IntPtr.Zero, (uint)trampolineSize,
                0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x40 /* PAGE_EXECUTE_READWRITE */);
            if (_trampolineAddr == IntPtr.Zero) { log.Warning("FontMapping: VirtualAlloc 失败"); return; }

            // 复制原始指令到 Trampoline
            Marshal.Copy(_savedBytes, 0, _trampolineAddr, PrologueSize);

            // 写入 JMP 回原函数 + PrologueSize
            WriteAbsoluteJump(_trampolineAddr + PrologueSize, _targetAddr + PrologueSize);

            _trampolineDelegate = Marshal.GetDelegateForFunctionPointer<LdFileDelegate>(_trampolineAddr);

            // 覆盖原函数入口 → JMP 到 HookHandler
            _hookDelegate = HookHandler;
            IntPtr hookAddr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);

            VirtualProtect(_targetAddr, (uint)PrologueSize, 0x40, out uint oldProtect);

            // 用 NOP 填充，然后写入 JMP
            byte[] hookPatch = new byte[PrologueSize];
            Array.Fill(hookPatch, (byte)0x90);
            WriteAbsoluteJumpBytes(hookPatch, 0, hookAddr);
            Marshal.Copy(hookPatch, 0, _targetAddr, PrologueSize);

            VirtualProtect(_targetAddr, (uint)PrologueSize, oldProtect, out _);

            _installed = true;
            log.Info("FontMapping: ldfile Hook 已安装");
        }
        catch (Exception ex)
        {
            log.Error("FontMapping: Hook 安装失败", ex);
        }
    }

    /// <summary>
    /// 卸载 Hook，恢复原始函数。
    /// </summary>
    internal static void Uninstall()
    {
        if (!_installed || _savedBytes == null) return;

        try
        {
            VirtualProtect(_targetAddr, (uint)PrologueSize, 0x40, out uint oldProtect);
            Marshal.Copy(_savedBytes, 0, _targetAddr, PrologueSize);
            VirtualProtect(_targetAddr, (uint)PrologueSize, oldProtect, out _);

            if (_trampolineAddr != IntPtr.Zero)
            {
                VirtualFree(_trampolineAddr, 0, 0x8000);
                _trampolineAddr = IntPtr.Zero;
            }

            _installed = false;
            LogService.Instance.Info("FontMapping: ldfile Hook 已卸载");
        }
        catch { }
    }

    /// <summary>
    /// 更新替换字体配置（用户通过 AFR 命令修改配置后调用）。
    /// </summary>
    internal static void UpdateConfig()
    {
        var config = ConfigService.Instance;
        _repMainFont = !string.IsNullOrEmpty(config.MainFont) ? EnsureShx(config.MainFont) : "";
        _repBigFont = !string.IsNullOrEmpty(config.BigFont) ? EnsureShx(config.BigFont) : "";
    }

    /// <summary>
    /// 获取本次会话的重定向记录，过滤掉样式表字体（供 AFRLOG 显示）。
    /// 样式表字体已由 FontReplacer 处理，用户可通过 ST/AFRLOG 调整，
    /// 此处仅返回样式表之外的重定向（即 MText 内联字体）。
    /// </summary>
    internal static List<InlineFontFixRecord> GetRedirectRecords(HashSet<string>? styleTableFontNames = null)
    {
        // 构建样式表排除集（规范化）
        HashSet<string>? exclude = null;
        if (styleTableFontNames is { Count: > 0 })
        {
            exclude = new(StringComparer.OrdinalIgnoreCase);
            foreach (string name in styleTableFontNames)
            {
                exclude.Add(EnsureShx(name).ToLowerInvariant());
                string noExt = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                if (!string.IsNullOrEmpty(noExt))
                    exclude.Add(noExt);
            }
        }

        var records = new List<InlineFontFixRecord>();
        foreach (var (missing, (replacement, fontType)) in _redirectLog)
        {
            // 过滤样式表缺失字体（精确匹配，不剥离 @ 前缀）
            if (exclude != null && exclude.Contains(missing))
                continue;

            string category = missing.StartsWith('@') ? "SHX大字体"
                : IsTrueTypeName(missing) ? "TrueType"
                : fontType == FontTypeBigFont ? "SHX大字体"
                : "SHX主字体";
            records.Add(new(missing, replacement, "MText映射", category));
        }
        return records;
    }

    #region Hook Handler

    // ldfile param2 字体类型常量（基于 AutoCAD 实际行为验证）
    // param2=0: SHX 大字体（Big Font）
    // param2=1: 常规 SHX 主字体
    // param2=2: SHX 形文件（Shape File）
    private const int FontTypeBigFont = 0;
    private const int FontTypeRegular = 1;
    private const int FontTypeShape = 2;

    private static int HookHandler(IntPtr fileName, int param2, IntPtr db, IntPtr desc)
    {
        // 防止递归
        if (_inHook || _trampolineDelegate == null)
            return _trampolineDelegate?.Invoke(fileName, param2, db, desc) ?? -1;

        _inHook = true;
        try
        {
            string fontName = Marshal.PtrToStringUni(fileName) ?? "";
            if (string.IsNullOrEmpty(fontName))
                return _trampolineDelegate(fileName, param2, db, desc);

            // 形文件请求 → 不拦截，由 AutoCAD 自行处理
            if (param2 == FontTypeShape)
                return _trampolineDelegate(fileName, param2, db, desc);

            string shxName = EnsureShx(fontName);
            bool fontExists = _availableFonts.Contains(fontName) || _availableFonts.Contains(shxName);

            // 字体文件存在 → 直接放行
            if (fontExists)
                return _trampolineDelegate(fileName, param2, db, desc);

            // 系统 TrueType 字族名放行 — ldfile 可能收到字族名（如 "宋体"），
            // 这些不在 _availableFonts（文件名集合）中但确实已安装，不应重定向。
            string baseName = fontName.TrimStart('@');
            if (FontDetector.IsSystemFont(baseName))
                return _trampolineDelegate(fileName, param2, db, desc);

            // 字体缺失 → 根据 param2 类型选择正确的替换字体
            string? resolved = ResolveMissingFont(fontName, param2);
            if (resolved != null)
            {
                string normalizedName = shxName.ToLowerInvariant();
                _redirectLog.TryAdd(normalizedName, (resolved, param2));

                IntPtr resolvedPtr = Marshal.StringToHGlobalUni(resolved);
                try
                {
                    return _trampolineDelegate(resolvedPtr, param2, db, desc);
                }
                finally
                {
                    Marshal.FreeHGlobal(resolvedPtr);
                }
            }

            return _trampolineDelegate(fileName, param2, db, desc);
        }
        catch
        {
            return _trampolineDelegate!(fileName, param2, db, desc);
        }
        finally
        {
            _inHook = false;
        }
    }

    /// <summary>
    /// 根据规则解析缺失字体的替换目标。
    /// param2 编码了 AutoCAD 期望的字体类型，决定使用 MainFont 还是 BigFont。
    /// </summary>
    private static string? ResolveMissingFont(string fontName, int fontType)
    {
        // @xxx → 优先尝试去掉 @ 的基础字体（支持 @@xxx 双前缀）
        if (fontName.StartsWith('@'))
        {
            string baseName = fontName.TrimStart('@');
            string baseShx = EnsureShx(baseName);
            if (_availableFonts.Contains(baseShx))
                return baseShx;
            // 基础字体不存在 → 回落到 param2 逻辑（不盲目用 BigFont）
            // @ 前缀可能是 TrueType 竖排（如 @Arial Unicode MS）或 SHX 大字体竖排
        }

        // TTF 字体通常不经过 ldfile，但如果出现则跳过（由系统字体 API 处理）
        if (IsTrueTypeName(fontName))
            return null;

        // 根据 param2 区分：AutoCAD 期望大字体 → 用 BigFont，期望主字体 → 用 MainFont
        if (fontType == FontTypeBigFont)
        {
            if (!string.IsNullOrEmpty(_repBigFont) && _availableFonts.Contains(_repBigFont))
                return _repBigFont;
            return null;
        }

        // 常规 SHX 缺失 → MainFont
        if (!string.IsNullOrEmpty(_repMainFont) && _availableFonts.Contains(_repMainFont))
            return _repMainFont;

        return null;
    }

    #endregion

    #region 字体扫描

    private static void ScanAvailableFonts()
    {
        // AutoCAD 安装目录 Fonts
        try
        {
            var acdbModule = System.Diagnostics.Process.GetCurrentProcess().Modules
                .Cast<System.Diagnostics.ProcessModule>()
                .FirstOrDefault(m => m.ModuleName?.Equals(AcDbDll, StringComparison.OrdinalIgnoreCase) == true);

            if (acdbModule?.FileName != null)
            {
                string fontsDir = Path.Combine(Path.GetDirectoryName(acdbModule.FileName)!, "Fonts");
                ScanDirectory(fontsDir);
            }
        }
        catch { }

        // 用户支持路径
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string dir in Directory.GetDirectories(
                Path.Combine(appData, "Autodesk"), "AutoCAD *", SearchOption.TopDirectoryOnly))
            {
                foreach (string supportDir in Directory.GetDirectories(dir, "Support", SearchOption.AllDirectories))
                    ScanDirectory(supportDir);
            }
        }
        catch { }

        // 系统 TrueType 字族名 — 同步扫描，确保 Hook 拦截前数据就绪
        // ldfile 可能收到字族名（如 "宋体"）而非文件名（simsun.ttc），
        // 必须将字族名加入可用集合，否则会被误判为缺失 SHX 并重定向。
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                _availableFonts.Add(family.Source);
                foreach (var localizedName in family.FamilyNames.Values)
                    _availableFonts.Add(localizedName);
            }
        }
        catch { }
    }

    private static void ScanDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".shx", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(file);
                    _availableFonts.Add(fileName);
                    ClassifyShxFont(file, fileName);
                }
                else if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                {
                    _availableFonts.Add(Path.GetFileName(file));
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 读取 SHX 文件头，识别是否为大字体文件。
    /// 文件头格式: "AutoCAD-86 bigfont 1.0" / "AutoCAD-86 unifont 1.0" / "AutoCAD-86 shapes 1.0"
    /// </summary>
    private static void ClassifyShxFont(string filePath, string fileName)
    {
        try
        {
            byte[] header = new byte[30];
            using var fs = File.OpenRead(filePath);
            int bytesRead = fs.Read(header, 0, 30);
            if (bytesRead < 25) return;

            string headerStr = System.Text.Encoding.ASCII.GetString(header, 0, bytesRead);
            if (headerStr.Contains("bigfont", StringComparison.OrdinalIgnoreCase))
                _bigFontFiles.Add(fileName);
        }
        catch { }
    }

    #endregion

    #region 底层工具

    private static string EnsureShx(string name) =>
        name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ? name : name + ".shx";

    private static bool IsTrueTypeName(string name) =>
        name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在指定内存地址写入 14 字节的绝对跳转 (FF 25 + 8 字节地址)。
    /// </summary>
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFree(IntPtr lpAddress, uint dwSize, uint dwFreeType);

    #endregion
}
