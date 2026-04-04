using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using System.Windows.Media;

using AFR.Models;

namespace AFR.Services;

/// <summary>
/// 检测图纸 TextStyleTable 中的缺失字体。
/// 支持 TrueType（系统字体 + CAD 搜索路径）、SHX（FindFile）、大字体（FindFile）。
/// 通过名称归一化处理 acad.fmp 字体映射问题。
/// FindFile 结果会话级缓存，避免重复磁盘 I/O。
/// </summary>
internal static class FontDetector
{
    // 系统字体索引 — 通过 Task.Run 在后台线程构建，避免阻塞 UI
    // 调用 PrewarmSystemFonts() 提前触发，Idle 时通常已就绪
    private static readonly Task<HashSet<string>> _systemFontNamesTask = Task.Run(BuildSystemFontIndex);

    // FindFile 结果缓存 — 字体可用性在 CAD 会话期间不变
    // Key: "{hint}:{normalizedFileName}" Value: 是否找到
    private static readonly ConcurrentDictionary<string, bool> _findFileCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 提前触发系统字体索引的后台构建。
    /// 应在插件初始化时尽早调用，使字体枚举与 CAD 启动并行执行。
    /// </summary>
    public static void PrewarmSystemFonts()
    {
        // 访问静态类即触发 _systemFontNamesTask 的 Task.Run
        // 此方法本身不阻塞 — 仅确保后台任务已启动
    }

    /// <summary>
    /// 检查名称是否为系统已安装的 TrueType 字体族名。
    /// 供 LdFileHook 在 ldfile 回调中判断：若为系统字体族名则放行，
    /// 避免将 TrueType 字族名（如 "宋体"）误当作缺失 SHX 文件重定向。
    /// </summary>
    public static bool IsSystemFont(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var task = _systemFontNamesTask;
        return task.IsCompletedSuccessfully && task.Result.Contains(name);
    }

    /// <summary>
    /// 清除 FindFile 和 SHX 分类缓存。
    /// 应在切换图纸上下文前调用，因为不同图纸的支持路径可能不同，
    /// 缓存的查找结果可能不适用于新图纸。
    /// </summary>
    public static void ClearCaches()
    {
        _findFileCache.Clear();
        _shxTypeCache.Clear();
    }

    /// <summary>
    /// 系统字体索引是否已成功构建。
    /// 用于清理逻辑在索引未就绪时安全跳过，避免因空索引导致误操作。
    /// </summary>
    public static bool IsSystemFontIndexReady
        => _systemFontNamesTask.IsCompletedSuccessfully
           && _systemFontNamesTask.Result.Count > 0;

    /// <summary>
    /// 扫描数据库中所有文字样式，返回存在缺失字体的样式列表。
    /// </summary>
    public static List<FontCheckResult> DetectMissingFonts(Database db)
    {
        var results = new List<FontCheckResult>();

        // 清除缓存，确保在当前图纸上下文中重新检测
        ClearCaches();

        using var tr = db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                var styleName = style.Name;
                var fileName = style.FileName ?? string.Empty;
                var bigFontName = style.BigFontFileName ?? string.Empty;
                var font = style.Font;

                // TrueType 字体可用性优先检查:
                // 当 TypeFace 指定的 TrueType 字体已安装时，AutoCAD 优先使用 TrueType 渲染，
                // 即使 FileName 指向的 SHX 文件缺失也不影响显示。
                // 此时不应将样式报告为缺失，否则 FontReplacer 会用 SHX 覆盖 TrueType → 乱码。
                if (!string.IsNullOrEmpty(font.TypeFace)
                    && IsTrueTypeFontAvailable(font.TypeFace, fileName, db))
                {
                    continue;
                }

                // 判断样式类型：SHX 还是 TrueType
                // 规则：TypeFace 非空 且 FileName 不是 SHX 格式 → TrueType
                //       其他情况 → SHX
                // 当 TypeFace 和 FileName 同时有值时（DWG 数据不一致），FileName 优先。
                // 此时 TrueType 已确认不可用（上方检查已排除），按 SHX 处理更安全。
                bool fileNameIsSHX = !string.IsNullOrWhiteSpace(fileName)
                    && !fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                    && !fileName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
                    && !fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
                bool isTrueType = !string.IsNullOrEmpty(font.TypeFace) && !fileNameIsSHX;
                bool isMainMissing = false;
                bool isBigMissing = false;

                if (isTrueType)
                {
                    isMainMissing = true; // TrueType 已确认不可用（上方 continue 排除了可用的）
                }
                else if (!string.IsNullOrWhiteSpace(fileName))
                {
                    // 检查主字体：文件必须存在且不能是大字体文件
                    isMainMissing = !IsShxFontAvailable(fileName, db)
                                 || IsShxTypeMismatch(fileName, db, expectBigFont: false);
                }

                // TrueType 样式无法使用大字体（AutoCAD 中大字体选项被禁用）
                if (!isTrueType && !string.IsNullOrWhiteSpace(bigFontName))
                {
                    // 检查大字体：文件必须存在且必须是大字体文件
                    isBigMissing = !IsShxFontAvailable(bigFontName, db)
                                || IsShxTypeMismatch(bigFontName, db, expectBigFont: true);
                }

                if (isMainMissing || isBigMissing)
                {
                    results.Add(new FontCheckResult(
                        styleName, fileName, bigFontName,
                        isMainMissing, isBigMissing, isTrueType,
                        isTrueType ? (font.TypeFace ?? string.Empty) : string.Empty));
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning($"检查样式时出错 {id}: {ex.Message}");
            }
        }

        tr.Commit();
        return results;
    }

    /// <summary>
    /// 收集样式表中所有字体文件名（含主字体和大字体）。
    /// 用于 Hook 排除列表：样式表字体由 FontReplacer 处理，不应被 Hook 拦截。
    /// </summary>
    public static HashSet<string> CollectStyleTableFontNames(Database db)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!string.IsNullOrWhiteSpace(style.FileName))
                    names.Add(style.FileName);
                if (!string.IsNullOrWhiteSpace(style.BigFontFileName))
                    names.Add(style.BigFontFileName);
            }
            catch { }
        }

        tr.Commit();
        return names;
    }

    /// <summary>
    /// 读取数据库中所有文字样式的当前字体配置。
    /// 用于 AFRLOG 界面显示实际字体（反映手动替换或 ST 命令修改后的状态）。
    /// </summary>
    public static Dictionary<string, (string FileName, string BigFontFileName, string TypeFace)>
        ReadCurrentFontAssignments(Database db)
    {
        var result = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

        using var tr = db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                result[style.Name] = (
                    style.FileName ?? string.Empty,
                    style.BigFontFileName ?? string.Empty,
                    style.Font.TypeFace ?? string.Empty
                );
            }
            catch { }
        }

        tr.Commit();
        return result;
    }

    /// <summary>
    /// 检查 SHX 字体文件是否可被 AutoCAD 找到。
    /// 通过名称归一化处理 acad.fmp 兼容性。
    /// </summary>
    internal static bool IsShxFontAvailable(string fileName, Database db)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;

        var normalized = NormalizeFontName(fileName);

        // 先用归一化后的名称尝试查找
        if (TryFindFile(normalized, db, FindFileHint.CompiledShapeFile))
            return true;

        // 若无扩展名，尝试添加 .shx
        if (!Path.HasExtension(normalized))
        {
            if (TryFindFile(normalized + ".shx", db, FindFileHint.CompiledShapeFile))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 检查 TrueType 字体是否通过系统字体或 AutoCAD 搜索路径可用。
    /// 仅需字体族名即可验证，用于替换前的可用性校验。
    /// </summary>
    internal static bool IsTrueTypeFontAvailable(string typeface, Database db)
        => IsTrueTypeFontAvailable(typeface, string.Empty, db);

    /// <summary>
    /// 检查 TrueType 字体是否通过系统字体或 AutoCAD 搜索路径可用。
    /// 第三方插件字体可能位于 CAD 支持路径而非系统目录。
    /// </summary>
    private static bool IsTrueTypeFontAvailable(string typeface, string fileName, Database db)
    {
        if (string.IsNullOrWhiteSpace(typeface)) return true;

        // 检查系统已安装字体（包含本地化名称）
        if (_systemFontNamesTask.IsCompletedSuccessfully
            && _systemFontNamesTask.Result.Contains(typeface))
            return true;

        // 若引用了具体字体文件名，通过 FindFile 检查
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var normalizedFile = NormalizeFontName(fileName);
            if (TryFindFile(normalizedFile, db, FindFileHint.TrueTypeFontFile))
                return true;
        }

        // 尝试使用字体名 + .ttf/.ttc 扩展名查找
        if (TryFindFile(typeface + ".ttf", db, FindFileHint.TrueTypeFontFile))
            return true;
        if (TryFindFile(typeface + ".ttc", db, FindFileHint.TrueTypeFontFile))
            return true;

        return false;
    }

    private static bool TryFindFile(string fileName, Database db, FindFileHint hint)
    {
        var cacheKey = string.Concat(((int)hint).ToString(), ":", fileName);

        if (_findFileCache.TryGetValue(cacheKey, out var cached))
            return cached;

        bool found;
        try
        {
            var result = HostApplicationServices.Current.FindFile(fileName, db, hint);
            found = !string.IsNullOrEmpty(result);
        }
        catch
        {
            found = false;
        }

        // 同时缓存正面和负面结果：
        // FindFile 未找到时抛出 eFileNotFound 异常，不缓存会导致异常风暴。
        // 跨图纸上下文安全由各入口处的 ClearCaches() 保证。
        _findFileCache.TryAdd(cacheKey, found);
        return found;
    }

    /// <summary>
    /// 查找字体文件并返回完整路径。
    /// </summary>
    private static string? TryFindFilePath(string fileName, Database db, FindFileHint hint)
    {
        try
        {
            string normalized = NormalizeFontName(fileName);
            string result = HostApplicationServices.Current.FindFile(normalized, db, hint);
            if (!string.IsNullOrEmpty(result)) return result;

            if (!Path.HasExtension(normalized))
            {
                result = HostApplicationServices.Current.FindFile(normalized + ".shx", db, hint);
                if (!string.IsNullOrEmpty(result)) return result;
            }
        }
        catch { }
        return null;
    }

    // SHX 类型分类缓存: filePath → isBigFont
    private static readonly ConcurrentDictionary<string, bool> _shxTypeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 检查 SHX 字体文件的类型是否与槽位不匹配。
    /// expectBigFont=true: 大字体槽位，文件应为 bigfont；
    /// expectBigFont=false: 主字体槽位，文件不应为 bigfont。
    /// </summary>
    private static bool IsShxTypeMismatch(string fileName, Database db, bool expectBigFont)
    {
        string? filePath = TryFindFilePath(fileName, db, FindFileHint.CompiledShapeFile);
        if (filePath == null) return false; // 文件不存在，由 IsShxFontAvailable 处理

        bool isBigFont = ClassifyShxFile(filePath);
        return expectBigFont != isBigFont;
    }

    /// <summary>
    /// 读取 SHX 文件头判断是否为大字体文件。
    /// 文件头 "AutoCAD-86 bigfont 1.0" = 大字体，其他 = 常规字体/形文件。
    /// </summary>
    private static bool ClassifyShxFile(string filePath)
    {
        if (_shxTypeCache.TryGetValue(filePath, out bool cached))
            return cached;

        bool isBigFont;
        try
        {
            byte[] header = new byte[30];
            using var fs = File.OpenRead(filePath);
            int bytesRead = fs.Read(header, 0, 30);
            if (bytesRead >= 25)
            {
                string headerStr = System.Text.Encoding.ASCII.GetString(header, 0, bytesRead);
                isBigFont = headerStr.Contains("bigfont", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                isBigFont = false;
            }
        }
        catch
        {
            // IO 异常不缓存 — 可能是临时文件锁定，下次重试可能成功
            return false;
        }

        _shxTypeCache.TryAdd(filePath, isBigFont);
        return isBigFont;
    }

    /// <summary>
    /// 归一化字体文件名以保证比较一致性。
    /// 去除路径前缀、修剪空白，处理 acad.fmp 映射兼容性。
    /// </summary>
    private static string NormalizeFontName(string name)
    {
        var trimmed = name.Trim();
        // 去除路径前缀 — 仅保留文件名
        return Path.GetFileName(trimmed);
    }

    /// <summary>
    /// 构建系统已安装字体名称的缓存索引，包含本地化变体。
    /// </summary>
    private static HashSet<string> BuildSystemFontIndex()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                names.Add(family.Source);
                foreach (var localizedName in family.FamilyNames.Values)
                {
                    names.Add(localizedName);
                }
            }
        }
        catch (Exception ex)
        {
            // 系统字体枚举失败 — 安全降级为空索引
            // TrueType 可用性判断和清理逻辑将据此跳过，避免误操作
            LogService.Instance.Error("系统字体索引构建失败，TrueType 可用性检查将不可靠", ex);
        }
        return names;
    }

    #region TrueType 字体特征查询

    // 缓存: fontName → (charset, pitchAndFamily)
    private static readonly ConcurrentDictionary<string, (int CharacterSet, int PitchAndFamily)>
        _fontMetricsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 查询 TrueType 字体的 CharacterSet 和 PitchAndFamily（LOGFONT 兼容值）。
    /// 这些值必须与 FontDescriptor 匹配，否则 AutoCAD 会"修正"内部状态，
    /// 导致 ST 弹出"该样式已修改"弹窗，且可能用错误的字符集渲染文字。
    /// </summary>
    public static (int CharacterSet, int PitchAndFamily) GetTrueTypeFontMetrics(string fontName)
    {
        if (string.IsNullOrEmpty(fontName))
            return (0, 0);

        if (_fontMetricsCache.TryGetValue(fontName, out var cached))
            return cached;

        var result = QueryFontMetricsFromGdi(fontName);
        _fontMetricsCache.TryAdd(fontName, result);
        LogService.Instance.Info($"[FontMetrics] '{fontName}' → CharacterSet={result.CharacterSet} PitchAndFamily={result.PitchAndFamily}");
        return result;
    }

    private static (int CharacterSet, int PitchAndFamily) QueryFontMetricsFromGdi(string fontName)
    {
        IntPtr hdc = IntPtr.Zero;
        IntPtr hFont = IntPtr.Zero;
        IntPtr oldFont = IntPtr.Zero;

        try
        {
            hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return (0, 0);

            hFont = CreateFontW(
                0, 0, 0, 0, 0, 0, 0, 0,
                1 /* DEFAULT_CHARSET */,
                0, 0, 0, 0, fontName);
            if (hFont == IntPtr.Zero) return (0, 0);

            oldFont = SelectObject(hdc, hFont);

            if (GetTextMetricsW(hdc, out var tm))
                return (tm.tmCharSet, tm.tmPitchAndFamily);

            return (0, 0);
        }
        catch
        {
            return (0, 0);
        }
        finally
        {
            if (oldFont != IntPtr.Zero && hdc != IntPtr.Zero)
                SelectObject(hdc, oldFont);
            if (hFont != IntPtr.Zero)
                DeleteObject(hFont);
            if (hdc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TEXTMETRICW
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading;
        public int tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang;
        public int tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality,
        uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW lptm);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    #endregion
}
