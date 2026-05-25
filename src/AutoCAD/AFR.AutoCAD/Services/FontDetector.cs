using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using AFR.FontMapping;
using AFR.Models;

namespace AFR.Services;

/// <summary>
/// 检测图纸 TextStyleTable 中的缺失字体。
/// <para>
/// 遍历所有文字样式，通过共享字体索引（SHX / TrueType）
/// 判断每个样式引用的主字体和大字体是否在当前环境中可用。
/// TrueType 度量缓存通过 <see cref="FontDetectionContext"/> 实例管理，只用于样式表写回。
/// </para>
/// </summary>
internal static class FontDetector
{
    /// <summary>预热系统 TrueType 字体索引。</summary>
    public static void PrewarmSystemFonts() => TrueTypeFontAvailabilityIndex.Initialize();

    /// <summary>
    /// 检查指定名称是否为已安装的系统 TrueType 字族名。
    /// @ 前缀会按基础字体名查询，不做 GDI vertical face 判断。
    /// </summary>
    public static bool IsSystemFont(string name) => TrueTypeFontAvailabilityIndex.IsAvailable(name);

    /// <summary>系统字体索引是否已构建完成且包含有效数据。</summary>
    public static bool IsSystemFontIndexReady => TrueTypeFontAvailabilityIndex.IsSystemIndexReady;

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
                // Path.GetFileName 剥离目录路径：DWG 中 style.FileName 可能存储
                // 旧版 CAD 安装目录的完整路径，归一化为纯文件名供显示和检测
                var fileName = Path.GetFileName(style.FileName ?? string.Empty);
                var bigFontName = Path.GetFileName(style.BigFontFileName ?? string.Empty);

                // 隔离 style.Font 访问 — 损坏的 TrueType 描述符不应阻断 SHX 检测
                FontDescriptor? safeFont = null;
                try { safeFont = style.Font; }
                catch (Exception fontEx)
                {
                    DiagnosticLogger.Skip(
                        "FontDetector",
                        "ReadFontDescriptor",
                        "TrueType 描述符损坏，已跳过 TrueType 验证",
                        new Dictionary<string, object?>
                        {
                            ["styleName"] = styleName,
                            ["error"] = fontEx.Message
                        });
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
                    DiagnosticLogger.Skip(
                        "FontDetector",
                        "DetectMissingFonts",
                        "跳过 ShapeFile 样式",
                        new Dictionary<string, object?>
                        {
                            ["styleName"] = styleName,
                            ["fileName"] = fileName
                        });
                    continue;
                }

                bool isMainMissing = false;
                bool isBigMissing = false;

                // TrueType 样式: 普通 face 验证字体可用才跳过；
                // @TrueType 先按去掉 @ 的基础字体判断，基础字体缺失时进入样式表回写。
                if (isTrueType)
                {
                    var typeFace = safeFont!.Value.TypeFace!;
                    if (FontRedirectResolver.HasAtPrefix(typeFace)
                        || FontRedirectResolver.HasAtPrefix(fileName))
                    {
                        string atFontName = FontRedirectResolver.HasAtPrefix(typeFace) ? typeFace : fileName;
                        if (TrueTypeFontAvailabilityIndex.IsAvailable(FontRedirectResolver.StripLeadingAtPrefix(atFontName)))
                        {
                            DiagnosticLogger.Skip(
                                "FontDetector",
                                "DetectMissingFonts",
                                "@TrueType 基础字体可用，跳过样式表替换",
                                new Dictionary<string, object?>
                                {
                                    ["styleName"] = styleName,
                                    ["typeFace"] = typeFace,
                                    ["fileName"] = fileName,
                                    ["baseFont"] = FontRedirectResolver.StripLeadingAtPrefix(atFontName)
                                });
                            continue;
                        }

                        isMainMissing = true;
                    }
                    else if (IsTrueTypeFontAvailable(typeFace, fileName, context))
                    {
                        DiagnosticLogger.LogFontAvailability(typeFace, "TrueType", true);
                        continue;
                    }
                    else
                    {
                        isMainMissing = true;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(fileName))
                {
                    isMainMissing = !IsShxFontAvailable(fileName, context) || IsShxTypeMismatch(fileName, context, expectBigFont: false);
                }
                if (!isTrueType && !string.IsNullOrWhiteSpace(bigFontName))
                {
                    isBigMissing = !IsShxFontAvailable(bigFontName, context) || IsShxTypeMismatch(bigFontName, context, expectBigFont: true);
                }
                if (isMainMissing || isBigMissing)
                {
                    DiagnosticLogger.LogMissing(styleName, isMainMissing, isBigMissing, isTrueType);
                    results.Add(new FontCheckResult(styleName, fileName, bigFontName, isMainMissing, isBigMissing, isTrueType, isTrueType ? (safeFont?.TypeFace ?? string.Empty) : string.Empty));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Fail(
                    "FontDetector",
                    "DetectMissingFonts",
                    "检查样式时出错",
                    ex,
                    new Dictionary<string, object?> { ["objectId"] = id.ToString() });
            }
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
    /// 通过共享 SHX 字体索引查找。
    /// </summary>
    internal static bool IsShxFontAvailable(string fileName, FontDetectionContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;
        return ShxFontAvailabilityIndex.IsExactAvailable(fileName);
    }

    /// <summary>检查 TrueType 字体是否可用（仅字族名版本，无 FileName 辅助）。</summary>
    internal static bool IsTrueTypeFontAvailable(string typeface, FontDetectionContext context)
        => IsTrueTypeFontAvailable(typeface, string.Empty, context);

    /// <summary>
    /// 检查 TrueType 字体是否可用。
    /// 通过共享 TrueType 字体索引查询；@ 前缀在索引中按基础字体处理。
    /// </summary>
    private static bool IsTrueTypeFontAvailable(string typeface, string fileName, FontDetectionContext context)
    {
        if (string.IsNullOrWhiteSpace(typeface)) return true;

        return TrueTypeFontAvailabilityIndex.IsAvailable(typeface)
               || (!string.IsNullOrWhiteSpace(fileName)
                   && TrueTypeFontAvailabilityIndex.IsAvailable(fileName));
    }

    /// <summary>
    /// 检查 SHX 字体文件的实际类型（主字体/大字体）是否与期望类型匹配。
    /// 不匹配时返回 true，表示虽然文件存在但类型错误（如主字体槽位引用了大字体文件）。
    /// </summary>
    internal static bool IsShxTypeMismatch(string fileName, FontDetectionContext context, bool expectBigFont)
    {
        if (!ShxFontAvailabilityIndex.IsExactAvailable(fileName))
            return false;

        // 分类失败 → 保守处理为不匹配，触发替换修复而非放行
        if (!ShxFontAvailabilityIndex.TryGetKind(fileName, out bool isBigFont))
            return true;

        return expectBigFont != isBigFont;
    }

    /// <summary>
    /// 判断文件名是否为 TrueType 字体文件（.ttf/.ttc/.otf）。
    /// </summary>
    internal static bool IsTrueTypeFontFile(string fileName)
        => !string.IsNullOrEmpty(fileName) &&
           (fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));

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
            DiagnosticLogger.Fail(
                "FontDetector",
                "GetTrueTypeFontMetrics",
                "GDI 字体度量查询失败，返回默认值且不缓存",
                fields: new Dictionary<string, object?> { ["fontName"] = fontName });
            return result;
        }
        context.FontMetricsCache.TryAdd(fontName, result);
        DiagnosticLogger.Ok(
            "FontDetector",
            "GetTrueTypeFontMetrics",
            "GDI 字体度量查询完成",
            new Dictionary<string, object?>
            {
                ["fontName"] = fontName,
                ["characterSet"] = result.CharacterSet,
                ["pitchAndFamily"] = result.PitchAndFamily
            });
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
