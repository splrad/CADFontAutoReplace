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
/// <para>
/// 遍历所有文字样式，通过多重验证（系统字体索引、FindFile、SHX 文件头分类）
/// 判断每个样式引用的主字体和大字体是否在当前环境中可用。
/// FindFile 和 TrueType 度量缓存通过 <see cref="FontDetectionContext"/> 实例管理，
/// SHX 类型分类缓存由全局 <see cref="FontManager.FontCache"/> 统一管理。
/// </para>
/// </summary>
internal static class FontDetector
{
    // 在后台线程异步构建系统字体索引（字族名集合），供 IsSystemFont 快速查询
    private static readonly Task<HashSet<string>> _systemFontNamesTask = Task.Run(BuildSystemFontIndex);

    // FindFile 缓存 key 前缀（预计算，避免每次调用 int.ToString()）
    private static readonly string FindFilePrefixShx = ((int)FindFileHint.CompiledShapeFile).ToString() + ":";
    private static readonly string FindFilePrefixTtf = ((int)FindFileHint.TrueTypeFontFile).ToString() + ":";

    /// <summary>预热系统字体索引。调用此方法会触发后台索引构建（如果尚未开始）。</summary>
    public static void PrewarmSystemFonts() { }

    /// <summary>
    /// 检查指定名称是否为已安装的系统 TrueType 字族名。
    /// 索引尚未就绪时返回 false（保守策略）。
    /// </summary>
    public static bool IsSystemFont(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var task = _systemFontNamesTask;
        return task.IsCompleted && !task.IsFaulted && task.Result.Contains(name);
    }

    /// <summary>系统字体索引是否已构建完成且包含有效数据。</summary>
    public static bool IsSystemFontIndexReady
        => _systemFontNamesTask.IsCompleted && !_systemFontNamesTask.IsFaulted
           && _systemFontNamesTask.Result.Count > 0;

    /// <summary>
    /// 检测指定数据库中所有文字样式的缺失字体。
    /// <para>
    /// 对每个样式判断：TrueType 字族名是否可用、SHX 主字体是否存在、
    /// SHX 大字体是否存在且类型匹配。ShapeFile 样式（用于复杂线型）自动跳过。
    /// </para>
    /// </summary>
    /// <param name="context">字体检测上下文，提供数据库引用和查询缓存。</param>
    /// <returns>缺失字体的检查结果列表，空列表表示无缺失。</returns>
    public static List<FontCheckResult> DetectMissingFonts(FontDetectionContext context)
    {
        var results = new List<FontCheckResult>();
        using var tr = context.Db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(context.Db.TextStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                var styleName = style.Name;
                var fileName = style.FileName ?? string.Empty;
                var bigFontName = style.BigFontFileName ?? string.Empty;

                // 隔离 style.Font 访问 — 损坏的 TrueType 描述符不应阻断 SHX 检测
                FontDescriptor? safeFont = null;
                try { safeFont = style.Font; }
                catch (Exception fontEx)
                {
                    DiagnosticLogger.Log("检测", $"样式 '{styleName}' 的 TrueType 描述符损坏，已跳过 TrueType 验证: {fontEx.Message}");
                }

                bool hasTT = safeFont.HasValue && !string.IsNullOrWhiteSpace(safeFont.Value.TypeFace);
                bool hasFile = !string.IsNullOrWhiteSpace(fileName);

                // FileName 为 TrueType 文件时，该样式仍属于 TrueType（AutoCAD 常同时写入 TypeFace 和 .ttf FileName）
                bool fileIsTrueType = hasFile && IsTrueTypeFontFile(fileName);
                bool isTrueType = hasTT && (!hasFile || fileIsTrueType);

                DiagnosticLogger.LogStyleScan(styleName, fileName, bigFontName,
                    safeFont.HasValue ? (safeFont.Value.TypeFace ?? "") : "<损坏>",
                    isTrueType, style.IsShapeFile);

                // ShapeFile 样式用于复杂线型（ltypeshp.shx 等），替换会破坏线型结构，
                // 且 FontReplacer 始终跳过此类样式，检测阶段直接排除避免产生永远无法消除的"未替换"条目
                if (style.IsShapeFile)
                {
                    DiagnosticLogger.Log("检测", $"跳过 ShapeFile 样式 '{styleName}'（FileName='{fileName}'）");
                    continue;
                }

                // TrueType 样式: 验证字体可用才跳过
                // IsTrueTypeFontAvailable 通过系统字体索引 + FindFile + 本地化反查三重验证
                if (isTrueType)
                {
                    var typeFace = safeFont!.Value.TypeFace!;
                    if (IsTrueTypeFontAvailable(typeFace, fileName, context))
                    {
                        DiagnosticLogger.LogFontAvailability(typeFace, "TrueType", true);
                        continue;
                    }
                }
                bool isMainMissing = false;
                bool isBigMissing = false;
                if (isTrueType)
                    isMainMissing = true;
                else if (!string.IsNullOrWhiteSpace(fileName))
                    isMainMissing = !IsShxFontAvailable(fileName, context) || IsShxTypeMismatch(fileName, context, expectBigFont: false);
                if (!isTrueType && !string.IsNullOrWhiteSpace(bigFontName))
                    isBigMissing = !IsShxFontAvailable(bigFontName, context) || IsShxTypeMismatch(bigFontName, context, expectBigFont: true);
                if (isMainMissing || isBigMissing)
                {
                    DiagnosticLogger.LogMissing(styleName, isMainMissing, isBigMissing, isTrueType);
                    results.Add(new FontCheckResult(styleName, fileName, bigFontName, isMainMissing, isBigMissing, isTrueType, isTrueType ? (safeFont?.TypeFace ?? string.Empty) : string.Empty));
                }
            }
            catch (Exception ex) { DiagnosticLogger.Log("检测", $"检查样式时出错 {id}: {ex.Message}"); }
        }
        tr.Commit();
        return results;
    }

    /// <summary>
    /// 收集数据库样式表中所有被引用的字体文件名（FileName + BigFontFileName）。
    /// 用于辅助判断哪些字体在图纸中被使用。
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
                if (!string.IsNullOrWhiteSpace(style.FileName)) names.Add(style.FileName);
                if (!string.IsNullOrWhiteSpace(style.BigFontFileName)) names.Add(style.BigFontFileName);
            }
            catch { }
        }
        tr.Commit();
        return names;
    }

    /// <summary>
    /// 读取数据库中所有文字样式当前实际的字体赋值（FileName、BigFontFileName、TypeFace）。
    /// 返回值反映替换后或 ST 命令修改后的最新状态，供 AFRLOG 界面展示。
    /// </summary>
    public static Dictionary<string, (string FileName, string BigFontFileName, string TypeFace)> ReadCurrentFontAssignments(Database db)
    {
        var result = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        using var tr = db.TransactionManager.StartTransaction();
        var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in styleTable)
        {
            try
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                string typeFace = string.Empty;
                try { typeFace = style.Font.TypeFace ?? string.Empty; } catch { }
                result[style.Name] = (style.FileName ?? string.Empty, style.BigFontFileName ?? string.Empty, typeFace);
            }
            catch { }
        }
        tr.Commit();
        return result;
    }

    /// <summary>
    /// 检查 SHX 字体文件是否在 AutoCAD 搜索路径中可用。
    /// 通过 <see cref="HostApplicationServices.FindFile"/> 查找，结果缓存在 context 中。
    /// </summary>
    internal static bool IsShxFontAvailable(string fileName, FontDetectionContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;
        var normalized = NormalizeFontName(fileName);
        if (TryFindFile(normalized, context, FindFileHint.CompiledShapeFile)) return true;
        if (!Path.HasExtension(normalized) && TryFindFile(normalized + ".shx", context, FindFileHint.CompiledShapeFile)) return true;
        return false;
    }

    /// <summary>检查 TrueType 字体是否可用（仅字族名版本，无 FileName 辅助）。</summary>
    internal static bool IsTrueTypeFontAvailable(string typeface, FontDetectionContext context)
        => IsTrueTypeFontAvailable(typeface, string.Empty, context);

    /// <summary>
    /// 检查 TrueType 字体是否可用。
    /// 依次通过：系统字体索引 → FindFile（FileName）→ FindFile（.ttf/.ttc）→ WPF 本地化名称反查。
    /// </summary>
    private static bool IsTrueTypeFontAvailable(string typeface, string fileName, FontDetectionContext context)
    {
        if (string.IsNullOrWhiteSpace(typeface)) return true;

        // 一次性快照任务状态，避免 TOCTOU 竞态：
        // 若在 FindFile 检查期间索引任务恰好完成，不快照会导致
        // 第一处检查（跳过索引）和第二处检查（跳过 WPF 回退）同时成立，
        // 使已安装的 TrueType 字体被误判为缺失。
        var task = _systemFontNamesTask;
        bool indexReady = task.IsCompleted && !task.IsFaulted;

        if (indexReady && task.Result.Contains(typeface)) return true;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            if (TryFindFile(NormalizeFontName(fileName), context, FindFileHint.TrueTypeFontFile)) return true;
        }
        if (TryFindFile(typeface + ".ttf", context, FindFileHint.TrueTypeFontFile)) return true;
        if (TryFindFile(typeface + ".ttc", context, FindFileHint.TrueTypeFontFile)) return true;

        // 本地化名称反查（同步降级）: 仅当方法入口时异步索引尚未就绪时执行 WPF 全量扫描。
        // 使用快照值 indexReady 而非重新读取 IsCompletedSuccessfully，
        // 确保索引未就绪时 WPF 回退始终执行，不会被竞态窗口跳过。
        if (!indexReady)
        {
            try
            {
                var family = System.Windows.Media.Fonts.SystemFontFamilies
                    .FirstOrDefault(f => f.FamilyNames.Values.Any(
                        n => string.Equals(n, typeface, StringComparison.OrdinalIgnoreCase)));
                if (family != null) return true;
            }
            catch { }
        }

        return false;
    }

    /// <summary>通过 HostApplicationServices.FindFile 查找字体文件，结果缓存在 context 中。</summary>
    private static bool TryFindFile(string fileName, FontDetectionContext context, FindFileHint hint)
    {
        var cacheKey = string.Concat(
            hint == FindFileHint.CompiledShapeFile ? FindFilePrefixShx : FindFilePrefixTtf,
            fileName);
        if (context.FindFileCache.TryGetValue(cacheKey, out var cached)) return cached;
        bool found;
        try { var r = HostApplicationServices.Current.FindFile(fileName, context.Db, hint); found = !string.IsNullOrEmpty(r); }
        catch { found = false; }
        context.FindFileCache.TryAdd(cacheKey, found);
        return found;
    }

    /// <summary>通过 FindFile 查找字体文件并返回完整路径，找不到返回 null。复用 FindFileCache 避免重复调用。</summary>
    private static string? TryFindFilePath(string fileName, FontDetectionContext context, FindFileHint hint)
    {
        string normalized = NormalizeFontName(fileName);

        string? path = FindFilePathCached(normalized, context, hint);
        if (path != null) return path;

        if (!Path.HasExtension(normalized))
        {
            path = FindFilePathCached(normalized + ".shx", context, hint);
            if (path != null) return path;
        }

        return null;
    }

    /// <summary>
    /// 单文件名的 FindFile 带缓存路径返回：
    /// 缓存已记录为不存在时直接短路；否则调用 FindFile 并回填 bool 缓存。
    /// </summary>
    private static string? FindFilePathCached(string normalized, FontDetectionContext context, FindFileHint hint)
    {
        var cacheKey = string.Concat(
            hint == FindFileHint.CompiledShapeFile ? FindFilePrefixShx : FindFilePrefixTtf,
            normalized);

        if (context.FindFileCache.TryGetValue(cacheKey, out var cached) && !cached)
            return null;

        try
        {
            string result = HostApplicationServices.Current.FindFile(normalized, context.Db, hint);
            if (!string.IsNullOrEmpty(result))
            {
                context.FindFileCache.TryAdd(cacheKey, true);
                return result;
            }
        }
        catch { }

        context.FindFileCache.TryAdd(cacheKey, false);
        return null;
    }

    /// <summary>
    /// 检查 SHX 字体文件的实际类型（主字体/大字体）是否与期望类型匹配。
    /// 不匹配时返回 true，表示虽然文件存在但类型错误（如主字体槽位引用了大字体文件）。
    /// </summary>
    internal static bool IsShxTypeMismatch(string fileName, FontDetectionContext context, bool expectBigFont)
    {
        string? filePath = TryFindFilePath(fileName, context, FindFileHint.CompiledShapeFile);
        if (filePath == null) return false;

        bool? classified = ClassifyShxFile(filePath);
        // 分类失败 → 保守处理为不匹配，触发替换修复而非放行
        if (!classified.HasValue) return true;

        return expectBigFont != classified.Value;
    }

    /// <summary>
    /// 判断 SHX 文件是否为大字体，优先查询全局 <see cref="FontManager.FontCache"/>，
    /// 未命中时通过 <see cref="ShxFontAnalyzer.IsBigFont"/> 读取文件头并回填缓存。
    /// 返回 null 表示文件读取失败（损坏、权限不足等），调用方应按保守策略处理。
    /// </summary>
    private static bool? ClassifyShxFile(string filePath)
    {
        string fileName = Path.GetFileName(filePath);

        // 优先查询全局缓存（缓存中只有确定性结果，命中即可信）
        if (FontManager.FontCache.TryGetValue(fileName, out bool cached))
            return cached;

        // 全局缓存未命中 → 读取文件头判断
        bool? result = ShxFontAnalyzer.IsBigFont(filePath);

        // 仅缓存确定性结果；读取失败（null）不写入缓存，下次访问时重试
        if (result.HasValue)
            FontManager.FontCache.TryAdd(fileName, result.Value);

        return result;
    }

    /// <summary>去除路径前缀，仅保留文件名并去除首尾空白。</summary>
    private static string NormalizeFontName(string name) => Path.GetFileName(name.Trim());

    /// <summary>
    /// 判断文件名是否为 TrueType 字体文件（.ttf/.ttc/.otf）。
    /// </summary>
    internal static bool IsTrueTypeFontFile(string fileName)
        => !string.IsNullOrEmpty(fileName) &&
           (fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 后台构建系统字体索引：枚举所有已安装的 TrueType 字族名（含本地化名称）。
    /// 逐字体容错，确保单个损坏字体不会中断后续索引。
    /// </summary>
    private static HashSet<string> BuildSystemFontIndex()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                try
                {
                    if (!string.IsNullOrEmpty(family.Source))
                        names.Add(family.Source);

                    foreach (var localizedName in family.FamilyNames.Values)
                    {
                        if (!string.IsNullOrEmpty(localizedName))
                            names.Add(localizedName);
                    }
                }
                catch
                {
                    // 跳过单个损坏字体，继续索引后续字体
                }
            }
        }
        catch (Exception ex) { DiagnosticLogger.LogError("系统字体索引构建失败", ex); }
        return names;
    }

    #region TrueType 字体特征查询

    /// <summary>
    /// 通过 GDI API 查询 TrueType 字体的 CharacterSet 和 PitchAndFamily 属性。
    /// 这些值用于构造 <see cref="FontDescriptor"/>，确保 AutoCAD 能正确匹配字体特征。
    /// </summary>
    public static (int CharacterSet, int PitchAndFamily) GetTrueTypeFontMetrics(string fontName, FontDetectionContext context)
    {
        if (string.IsNullOrEmpty(fontName)) return (0, 0);
        if (context.FontMetricsCache.TryGetValue(fontName, out var cached)) return cached;
        var result = QueryFontMetricsFromGdi(fontName);
        if (result.CharacterSet == 0 && result.PitchAndFamily == 0)
        {
            DiagnosticLogger.Log("FontMetrics", $"'{fontName}' GDI 查询失败，返回默认值 (0,0)，未缓存");
            return result;
        }
        context.FontMetricsCache.TryAdd(fontName, result);
        DiagnosticLogger.Log("FontMetrics", $"'{fontName}' CharSet={result.CharacterSet} Pitch={result.PitchAndFamily}");
        return result;
    }

    /// <summary>通过 Win32 GDI API 查询指定字体的 TEXTMETRIC 信息。</summary>
    private static (int CharacterSet, int PitchAndFamily) QueryFontMetricsFromGdi(string fontName)
    {
        IntPtr hdc = IntPtr.Zero, hFont = IntPtr.Zero, oldFont = IntPtr.Zero;
        try
        {
            hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return (0, 0);
            hFont = CreateFontW(0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, fontName);
            if (hFont == IntPtr.Zero) return (0, 0);
            oldFont = SelectObject(hdc, hFont);
            if (GetTextMetricsW(hdc, out var tm)) return (tm.tmCharSet, tm.tmPitchAndFamily);
            return (0, 0);
        }
        catch { return (0, 0); }
        finally
        {
            if (oldFont != IntPtr.Zero && hdc != IntPtr.Zero) SelectObject(hdc, oldFont);
            if (hFont != IntPtr.Zero) DeleteObject(hFont);
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
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

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW lptm);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);

    #endregion
}