using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 通过 Hook acdb DLL 的 ldfile 函数，在 DWG 文件解析阶段拦截字体文件加载请求。
/// <para>
/// 设计原理（两阶段协作）：
/// <list type="bullet">
///   <item>DWG 解析阶段（本类负责）：Hook 拦截字体加载，按类型分流重定向 —
///     .shx 后缀 → 用配置的 SHX 替换字体（MainFont / BigFont）；
///     非 .shx（TrueType 字族名）→ 用配置的 TrueType 替换字体。</item>
///   <item>Execute 阶段（FontReplacer 负责）：覆盖样式表字体，用户可通过 ST/AFRLOG 随时调整。</item>
/// </list>
/// </para>
/// <para>
/// 关键约束：对 TrueType 字族名必须重定向到 TrueType 替换字体，而非 SHX。
/// 若将 TrueType 误重定向为 SHX，会污染 AutoCAD 内部字体缓存，
/// 导致 FontReplacer 的 TrueType 替换与缓存冲突（表现为文字乱码 + ST 弹窗）。
/// </para>
/// </summary>
internal static class LdFileHook
{
    // 从平台常量获取目标 DLL 名、导出函数名、序言长度
    private static string AcDbDll => PlatformManager.Platform.AcDbDllName;
    private static string LdFileExport => PlatformManager.Platform.LdFileExport;
    private static int PrologueSize => PlatformManager.Platform.PrologueSize;

    // ldfile 原始函数签名：int ldfile(wchar_t* fileName, int param2, void* db, void* desc)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LdFileDelegate(IntPtr fileName, int param2, IntPtr db, IntPtr desc);

    // ── Hook 基础设施 ──
    private static LdFileDelegate? _hookDelegate;       // 指向 HookHandler 的委托（防 GC 回收）
    private static LdFileDelegate? _trampolineDelegate;  // 指向 Trampoline 的委托（用于调用原始函数）
    private static IntPtr _targetAddr;                   // 原始 ldfile 函数地址
    private static IntPtr _trampolineAddr;               // Trampoline 内存地址
    private static byte[]? _savedBytes;                  // 被覆盖的原始字节（卸载时恢复）
    private static volatile bool _installed;

    internal static bool IsInstalled => _installed;

    // ── 字体解析状态 ──
    // 可用字体集合：包含 SHX 文件名 + TrueType 文件名 + 系统字族名
    private static readonly HashSet<string> _availableFonts = new(StringComparer.OrdinalIgnoreCase);
    // 用户配置的替换字体（运行时副本）
    private static string _repMainFont = "";
    private static string _repBigFont = "";
    private static string _repTrueTypeFont = "";
    // 防递归标志：避免 HookHandler 内部调用 Trampoline 时再次触发 Hook
    [ThreadStatic] private static bool _inHook;

    // 重定向日志：记录本次会话中所有被重定向的字体，供 MText 内联字体交叉比对
    // Key: 归一化字体名（小写 + .shx 后缀 / TrueType 原名）
    // Value: (替换字体名, ldfile param2 字体类型)
    private static readonly ConcurrentDictionary<string, (string Replacement, int FontType)> _redirectLog = new(StringComparer.OrdinalIgnoreCase);

    // 重定向字体名的原生指针缓存 — 必须保持存活，绝对不能释放
    // 原因：ldfile 可能将 fileName 指针存入 AcFontDescription 或全局字体表，
    // 若释放则成为悬空指针，后续字体类型判断会读取垃圾数据。
    private static readonly ConcurrentDictionary<string, IntPtr> _nativeStringCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 安装 ldfile Hook。
    /// <para>
    /// 通过 Inline Hook 技术覆盖 ldfile 函数入口：
    /// 保存原始指令 → 创建 Trampoline（原始指令 + 跳回）→ 覆盖入口为跳转到 HookHandler。
    /// 必须在任何文档打开之前调用（在 PluginEntry.Initialize 中执行）。
    /// </para>
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;

        try
        {
            IntPtr module = GetModuleHandle(AcDbDll);
            if (module == IntPtr.Zero) { DiagnosticLogger.Log("FontMapping", "acdb25.dll 未加载"); return; }

            _targetAddr = GetProcAddress(module, LdFileExport);
            if (_targetAddr == IntPtr.Zero) { DiagnosticLogger.Log("FontMapping", "未找到 ldfile 导出"); return; }

            // 加载用户配置的替换字体（SHX 需要确保 .shx 后缀）
            var config = ConfigService.Instance;
            if (!string.IsNullOrEmpty(config.MainFont))
                _repMainFont = EnsureShx(config.MainFont);
            if (!string.IsNullOrEmpty(config.BigFont))
                _repBigFont = EnsureShx(config.BigFont);
            if (!string.IsNullOrEmpty(config.TrueTypeFont))
                _repTrueTypeFont = config.TrueTypeFont;

            if (string.IsNullOrEmpty(_repMainFont) && string.IsNullOrEmpty(_repBigFont))
            {
                DiagnosticLogger.Log("FontMapping", "未配置替换字体，Hook 未安装");
                return;
            }

            // 扫描系统中可用的字体文件，构建 _availableFonts 集合并填充 FontManager.FontCache
            ScanAvailableFonts();

            // --- Inline Hook 安装流程 ---
            // 第一步：保存原始函数入口的指令字节（卸载时恢复）
            _savedBytes = new byte[PrologueSize];
            Marshal.Copy(_targetAddr, _savedBytes, 0, PrologueSize);

            // 第二步：创建 Trampoline（跳板）— 一小块可执行内存，包含原始指令 + 跳回原函数
            int trampolineSize = PrologueSize + 14; // 14 字节 = 64 位绝对跳转指令
            _trampolineAddr = VirtualAlloc(IntPtr.Zero, (uint)trampolineSize,
                0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x40 /* PAGE_EXECUTE_READWRITE */);
            if (_trampolineAddr == IntPtr.Zero) { DiagnosticLogger.Log("FontMapping", "VirtualAlloc 失败"); return; }

            // 将原始指令复制到 Trampoline 头部
            Marshal.Copy(_savedBytes, 0, _trampolineAddr, PrologueSize);

            // 在 Trampoline 尾部写入跳转指令，跳回原函数被覆盖部分之后的位置
            WriteAbsoluteJump(_trampolineAddr + PrologueSize, _targetAddr + PrologueSize);

            // 将 Trampoline 地址包装为委托，供 HookHandler 调用原始函数
            _trampolineDelegate = Marshal.GetDelegateForFunctionPointer<LdFileDelegate>(_trampolineAddr);

            // 第三步：覆盖原函数入口 → 跳转到 HookHandler
            _hookDelegate = HookHandler;
            IntPtr hookAddr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);

            // 修改原函数入口的内存保护属性为可读写可执行
            VirtualProtect(_targetAddr, (uint)PrologueSize, 0x40, out uint oldProtect);

            // 先用 NOP 填充整个序言区域，再写入跳转指令（确保不留残余的旧指令）
            byte[] hookPatch = new byte[PrologueSize];
            Array.Fill(hookPatch, (byte)0x90);
            WriteAbsoluteJumpBytes(hookPatch, 0, hookAddr);
            Marshal.Copy(hookPatch, 0, _targetAddr, PrologueSize);

            // 恢复原始内存保护属性
            VirtualProtect(_targetAddr, (uint)PrologueSize, oldProtect, out _);

            _installed = true;
            DiagnosticLogger.Log("FontMapping", "ldfile Hook 已安装");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError("FontMapping: Hook 安装失败", ex);
        }
    }

    /// <summary>
    /// 卸载 Hook，将原始函数入口恢复为安装前的字节，并释放 Trampoline 内存。
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
            DiagnosticLogger.Log("FontMapping", "ldfile Hook 已卸载");
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

    // ldfile param2 字体类型常量（基于 AutoCAD 2026 实测验证）
    private const int FontTypeRegular = 0;   // 常规 SHX 主字体
    private const int FontTypeShape = 2;     // SHX 形文件（Shape File，如线型符号）
    private const int FontTypeBigFont = 4;   // SHX 大字体（Big Font，用于东亚字符）

    /// <summary>
    /// Hook 核心处理函数：拦截每次字体文件加载请求，判断是否需要重定向。
    /// <para>
    /// 处理流程：防递归检查 → 形文件放行 → 字体存在性检查 → 按类型选择替换策略。
    /// </para>
    /// </summary>
    private static int HookHandler(IntPtr fileName, int param2, IntPtr db, IntPtr desc)
    {
        // 防止递归：HookHandler 通过 Trampoline 调用原始函数时，不应再次触发 Hook
        if (_inHook || _trampolineDelegate == null)
            return _trampolineDelegate?.Invoke(fileName, param2, db, desc) ?? -1;

        _inHook = true;
        try
        {
            string fontName = Marshal.PtrToStringUni(fileName) ?? "";
            if (string.IsNullOrEmpty(fontName))
                return _trampolineDelegate(fileName, param2, db, desc);

            // 剥离目录路径，仅保留文件名或字族名
            // DWG 中 style.FileName 和 MText \F 格式代码可能存储来自旧版 AutoCAD
            // 安装目录的完整路径（如 "C:/Software/Autodesk/AutoCAD 2024/Support/txt.shx"），
            // 但 _availableFonts 仅含纯文件名，未归一化会导致可用字体被误判为缺失。
            // Path.GetFileName 对纯文件名和字族名（如 "宋体"）是无操作，安全适用。
            fontName = Path.GetFileName(fontName);

            // 形文件请求 → 不拦截，由 AutoCAD 自行处理
            if (param2 == FontTypeShape)
                return _trampolineDelegate(fileName, param2, db, desc);

            // 字体存在性检查 — 先用原名查找（零分配快速路径），
            // 仅在未命中且名称不以 .shx 结尾时才拼接后缀重试
            if (_availableFonts.Contains(fontName))
                return _trampolineDelegate(fileName, param2, db, desc);

            bool isShxRequest = fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
            if (!isShxRequest && _availableFonts.Contains(fontName + ".shx"))
                return _trampolineDelegate(fileName, param2, db, desc);

            // 系统 TrueType 字族名放行（复用 baseName 避免后续重复 TrimStart）
            bool hasAtPrefix = fontName[0] == '@';
            string baseName = hasAtPrefix ? fontName.TrimStart('@') : fontName;
            if (FontDetector.IsSystemFont(baseName))
                return _trampolineDelegate(fileName, param2, db, desc);

            // 字体缺失 → 按字体类型选择替换策略
            // SHX 主字体（param2=0）和大字体（param2=4）→ Hook 统一重定向
            //   注意: AutoCAD 可能传入不带 .shx 后缀的小写字体名（如 'noexistshx'、'2'）
            // TrueType（非 SHX 类型请求）→ Hook 处理（FONTALT 不处理 TrueType 字族名）

            // SHX 字体判定: param2 明确为 SHX 类型（主字体/大字体），或文件名以 .shx 结尾
            bool isShxFont = param2 == FontTypeRegular || param2 == FontTypeBigFont || isShxRequest;
            string? resolved = isShxFont
                ? ResolveMissingShxFont(fontName, param2)
                : ResolveMissingTrueTypeFont(fontName);
            if (resolved != null)
            {
                // 归一化 key: SHX 字体确保 .shx 后缀（AutoCAD 可能传入无后缀的名称如 'noexistshx'），
                // TrueType 使用去 @ 后的字族名。两者均与 MTextFontParser 的归一化规则对齐。
                string normalizedName = isShxFont ? EnsureShx(baseName) : baseName;
                _redirectLog.TryAdd(normalizedName, (resolved, param2));

                DiagnosticLogger.Log("Hook", $"重定向: '{fontName}' param2={param2} → '{resolved}'");

                // 获取或创建原生字符串指针（缓存，不释放）
                // ldfile 可能将 fileName 指针存入 AcFontDescription 供后续大字体绑定查找，
                // 若使用临时指针并释放，后续读取悬空指针会导致字体类型判断错误，
                // 产生 "常规字体文件，不是大字体文件" 警告。
                IntPtr resolvedPtr = _nativeStringCache.GetOrAdd(resolved,
                    static name => Marshal.StringToHGlobalUni(name));

                return _trampolineDelegate(resolvedPtr, param2, db, desc);
            }

            DiagnosticLogger.Log("Hook", $"未解析: '{fontName}' param2={param2} isShx={isShxRequest}");
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
    /// 解析缺失 SHX 字体的替换目标。
    /// param2 编码了 AutoCAD 期望的字体类型（0=主字体, 4=大字体），决定使用 MainFont 还是 BigFont。
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
                if (fontType != FontTypeBigFont || (FontManager.FontCache.TryGetValue(baseShx, out bool isBaseBig) && isBaseBig))
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

            foreach (var kvp in FontManager.FontCache)
            {
                if (kvp.Value && _availableFonts.Contains(kvp.Key))
                    return kvp.Key;
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

    /// <summary>
    /// 扫描所有可能包含字体文件的目录，构建可用字体集合。
    /// 扫描范围：通过 <see cref="CadEnvironmentSettings.GetAllFontSearchPaths"/> 获取统一路径列表，
    /// 再补充系统 TrueType 字族名。
    /// </summary>
    private static void ScanAvailableFonts()
    {
        // 统一路径扫描：SHX + TTF/TTC 文件
        foreach (var dir in CadEnvironmentSettings.GetAllFontSearchPaths())
            ScanDirectory(dir);

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
            DiagnosticLogger.Log("FontMapping", $"系统字体扫描失败: {ex.Message}");
        }
        DiagnosticLogger.Log("FontMapping", $"可用字体 {_availableFonts.Count} 项 (系统字族名 {_availableFonts.Count - beforeCount} 项)");
    }

    /// <summary>
    /// 扫描指定目录中的字体文件（.shx / .ttf / .ttc），将文件名加入可用字体集合。
    /// SHX 文件还会通过 <see cref="ClassifyShxFont"/> 读取文件头判断是否为大字体。
    /// </summary>
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
    /// 读取 SHX 文件头，识别是否为大字体文件，结果写入全局 <see cref="FontManager.FontCache"/>。
    /// 文件不可读时跳过，不写入缓存（下次访问时重试）。
    /// </summary>
    private static void ClassifyShxFont(string filePath, string fileName)
    {
        if (FontManager.FontCache.ContainsKey(fileName)) return;
        bool? result = ShxFontAnalyzer.IsBigFont(filePath);
        if (result.HasValue)
            FontManager.FontCache.TryAdd(fileName, result.Value);
    }

    #endregion

    #region 底层工具

    /// <summary>确保字体名以 .shx 后缀结尾（若已有则不重复添加）。</summary>
    private static string EnsureShx(string name) =>
        name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ? name : name + ".shx";

    /// <summary>判断字体名是否为 TrueType 文件名（以 .ttf / .ttc / .otf 结尾）。</summary>
    private static bool IsTrueTypeName(string name) =>
        name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在指定内存地址写入 14 字节的 64 位绝对跳转指令。
    /// 指令格式: FF 25 00 00 00 00 [8字节目标地址]（JMP [RIP+0]，后跟内联地址）。
    /// </summary>
    private static void WriteAbsoluteJump(IntPtr location, IntPtr target)
    {
        byte[] jmp = new byte[14];
        WriteAbsoluteJumpBytes(jmp, 0, target);
        Marshal.Copy(jmp, 0, location, 14);
    }

    /// <summary>将 14 字节绝对跳转指令写入字节数组的指定偏移位置。</summary>
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
