using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// Hook acdb25.dll 的 ldfile 函数，在 DWG 解析阶段拦截缺失字体文件加载。
///
/// 设计原理：
///   DWG 解析阶段: Hook 按字体类型分流重定向 —
///     .shx 后缀 → 用配置的 SHX 替换字体（MainFont / BigFont）
///     非 .shx（TrueType 字族名）→ 用配置的 TrueType 替换字体
///   Execute 阶段: FontReplacer 覆盖样式表字体，用户可通过 ST/AFRLOG 随时调整。
///
/// 关键约束：
///   对 TrueType 字族名（样式表回退或 MText 内联 \f）必须重定向到 TrueType 替换字体，
///   而非 SHX。若将 TrueType 误重定向为 SHX，会污染 AutoCAD 内部字体缓存，
///   导致 FontReplacer 的 TrueType 替换与缓存冲突（文字乱码 + ST 弹窗）。
/// </summary>
internal static class LdFileHook
{
    private static string AcDbDll => PlatformManager.Platform.AcDbDllName;
    private static string LdFileExport => PlatformManager.Platform.LdFileExport;
    private static int PrologueSize => PlatformManager.Platform.PrologueSize;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LdFileDelegate(IntPtr fileName, int param2, IntPtr db, IntPtr desc);

    // Hook 基础设施
    private static LdFileDelegate? _hookDelegate;
    private static LdFileDelegate? _trampolineDelegate;
    private static IntPtr _targetAddr;
    private static IntPtr _trampolineAddr;
    private static byte[]? _savedBytes;
    private static volatile bool _installed;

    internal static bool IsInstalled => _installed;

    // 字体解析状态
    private static readonly HashSet<string> _availableFonts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _bigFontFiles = new(StringComparer.OrdinalIgnoreCase);
    private static string _repMainFont = "";
    private static string _repBigFont = "";
    private static string _repTrueTypeFont = "";
    [ThreadStatic] private static bool _inHook;

    // 记录本次会话的重定向: fontName → (replacement, fontType)
    private static readonly ConcurrentDictionary<string, (string Replacement, int FontType)> _redirectLog = new(StringComparer.OrdinalIgnoreCase);

    // 重定向字体名的原生指针缓存 — 必须保持存活，不能释放
    // ldfile 可能将 fileName 指针存入 AcFontDescription 或全局字体表，
    // 若释放则成为悬空指针，导致后续字体类型判断读取垃圾数据。
    private static readonly ConcurrentDictionary<string, IntPtr> _nativeStringCache = new(StringComparer.OrdinalIgnoreCase);

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
            if (!string.IsNullOrEmpty(config.TrueTypeFont))
                _repTrueTypeFont = config.TrueTypeFont;

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

            // 创建 Trampoline
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
        _repTrueTypeFont = !string.IsNullOrEmpty(config.TrueTypeFont) ? config.TrueTypeFont : "";
    }

    /// <summary>
    /// 获取本次会话的原始重定向记录（供 MText 内联字体交叉比对）。
    /// Key: 归一化字体名（小写，含 .shx 后缀）
    /// Value: (替换字体, ldfile param2 字体类型)
    /// </summary>
    internal static IReadOnlyDictionary<string, (string Replacement, int FontType)> GetRawRedirectLog()
        => _redirectLog;

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

            // 系统 TrueType 字族名放行
            string baseName = fontName.TrimStart('@');
            if (FontDetector.IsSystemFont(baseName))
                return _trampolineDelegate(fileName, param2, db, desc);

            // 字体缺失 → 按字体类型选择替换策略
            // .shx 后缀 → SHX 字体，用 MainFont/BigFont 替换
            // 非 .shx → TrueType 字族名（样式表回退或内联 \f），用 TrueTypeFont 替换
            bool isShxRequest = fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
            string? resolved = isShxRequest
                ? ResolveMissingShxFont(fontName, param2)
                : ResolveMissingTrueTypeFont(fontName);
            if (resolved != null)
            {
                string normalizedName = isShxRequest
                    ? shxName.ToLowerInvariant()
                    : fontName.TrimStart('@');
                _redirectLog.TryAdd(normalizedName, (resolved, param2));

                // 获取或创建原生字符串指针（缓存，不释放）
                // ldfile 可能将 fileName 指针存入 AcFontDescription 供后续大字体绑定查找，
                // 若使用临时指针并释放，后续读取悬空指针会导致字体类型判断错误，
                // 产生 "常规字体文件，不是大字体文件" 警告。
                IntPtr resolvedPtr = _nativeStringCache.GetOrAdd(resolved,
                    static name => Marshal.StringToHGlobalUni(name));

                return _trampolineDelegate(resolvedPtr, param2, db, desc);
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
    /// 解析缺失 SHX 字体的替换目标（fontName 以 .shx 结尾）。
    /// param2 编码了 AutoCAD 期望的字体类型，决定使用 MainFont 还是 BigFont。
    /// </summary>
    private static string? ResolveMissingShxFont(string fontName, int fontType)
    {
        // @xxx.shx → 优先尝试去掉 @ 的基础字体（支持 @@xxx 双前缀）
        if (fontName.StartsWith('@'))
        {
            string baseName = fontName.TrimStart('@');
            string baseShx = EnsureShx(baseName);
            if (_availableFonts.Contains(baseShx))
            {
                if (fontType != FontTypeBigFont || _bigFontFiles.Contains(baseShx))
                    return baseShx;
            }
        }

        // TTF 文件名（如 arial.ttf）→ 跳过，由系统字体 API 处理
        if (IsTrueTypeName(fontName))
            return null;

        // 根据 param2 区分：大字体 → BigFont，主字体 → MainFont
        if (fontType == FontTypeBigFont)
        {
            if (!string.IsNullOrEmpty(_repBigFont) && _availableFonts.Contains(_repBigFont))
                return _repBigFont;

            foreach (var bf in _bigFontFiles)
            {
                if (_availableFonts.Contains(bf))
                    return bf;
            }
            return null;
        }

        if (!string.IsNullOrEmpty(_repMainFont) && _availableFonts.Contains(_repMainFont))
            return _repMainFont;

        return null;
    }

    /// <summary>
    /// 解析缺失 TrueType 字体的替换目标（fontName 无 .shx 后缀）。
    /// 来源：样式表缺失 TrueType 的 AutoCAD 回退调用，或 MText 内联 \f 字体。
    /// 必须用 TrueType 字族名替换，避免 SHX 类型污染 AutoCAD 内部缓存。
    /// </summary>
    private static string? ResolveMissingTrueTypeFont(string fontName)
    {
        // @xxx → 优先尝试去掉 @ 的基础字族名（竖排 TrueType）
        if (fontName.StartsWith('@'))
        {
            string baseName = fontName.TrimStart('@');
            if (FontDetector.IsSystemFont(baseName))
                return baseName;
        }

        // 用用户配置的 TrueType 替换字体
        if (!string.IsNullOrEmpty(_repTrueTypeFont) && FontDetector.IsSystemFont(_repTrueTypeFont))
            return _repTrueTypeFont;

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
        int beforeCount = _availableFonts.Count;
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                _availableFonts.Add(family.Source);
                foreach (var localizedName in family.FamilyNames.Values)
                    _availableFonts.Add(localizedName);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"FontMapping: 系统字体扫描失败: {ex.Message}");
        }
        LogService.Instance.Info($"FontMapping: 可用字体 {_availableFonts.Count} 项 (系统字族名 {_availableFonts.Count - beforeCount} 项)");

        // Windows 系统字体目录 — ldfile 可能收到系统字体文件名（如 simsun.ttc），
        // 这些文件位于 C:\Windows\Fonts 而非 AutoCAD 目录，必须扫描以避免误重定向。
        try
        {
            ScanDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
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
