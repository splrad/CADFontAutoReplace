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
/// 所有缓存通过 FontDetectionContext 实例管理，单次事务结束后由 GC 回收。
/// </summary>
internal static class FontDetector
{
    private static readonly Task<HashSet<string>> _systemFontNamesTask = Task.Run(BuildSystemFontIndex);

    public static void PrewarmSystemFonts() { }

    public static bool IsSystemFont(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var task = _systemFontNamesTask;
        return task.IsCompletedSuccessfully && task.Result.Contains(name);
    }

    public static bool IsSystemFontIndexReady
        => _systemFontNamesTask.IsCompletedSuccessfully
           && _systemFontNamesTask.Result.Count > 0;

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
                    LogService.Instance.Warning($"样式 '{styleName}' 的 TrueType 描述符损坏，已跳过 TrueType 验证: {fontEx.Message}");
                }

                bool hasTT = safeFont.HasValue && !string.IsNullOrEmpty(safeFont.Value.TypeFace);
                bool hasFile = !string.IsNullOrWhiteSpace(fileName);

                // FileName 为 TrueType 文件时，该样式仍属于 TrueType（AutoCAD 常同时写入 TypeFace 和 .ttf FileName）
                bool fileIsTrueType = hasFile &&
                    (fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
                bool isTrueType = hasTT && (!hasFile || fileIsTrueType);

                DiagnosticLogger.LogStyleScan(styleName, fileName, bigFontName,
                    safeFont.HasValue ? (safeFont.Value.TypeFace ?? "") : "<损坏>",
                    isTrueType, style.IsShapeFile);

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
            catch (Exception ex) { LogService.Instance.Warning($"检查样式时出错 {id}: {ex.Message}"); }
        }
        tr.Commit();
        return results;
    }

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

    internal static bool IsShxFontAvailable(string fileName, FontDetectionContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;
        var normalized = NormalizeFontName(fileName);
        if (TryFindFile(normalized, context, FindFileHint.CompiledShapeFile)) return true;
        if (!Path.HasExtension(normalized) && TryFindFile(normalized + ".shx", context, FindFileHint.CompiledShapeFile)) return true;
        return false;
    }

    internal static bool IsTrueTypeFontAvailable(string typeface, FontDetectionContext context)
        => IsTrueTypeFontAvailable(typeface, string.Empty, context);

    private static bool IsTrueTypeFontAvailable(string typeface, string fileName, FontDetectionContext context)
    {
        if (string.IsNullOrWhiteSpace(typeface)) return true;
        if (_systemFontNamesTask.IsCompletedSuccessfully && _systemFontNamesTask.Result.Contains(typeface)) return true;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            if (TryFindFile(NormalizeFontName(fileName), context, FindFileHint.TrueTypeFontFile)) return true;
        }
        if (TryFindFile(typeface + ".ttf", context, FindFileHint.TrueTypeFontFile)) return true;
        if (TryFindFile(typeface + ".ttc", context, FindFileHint.TrueTypeFontFile)) return true;

        // 本地化名称反查: 通过 WPF 字体家族匹配本地名称（如 "宋体" → "SimSun"）
        try
        {
            var family = System.Windows.Media.Fonts.SystemFontFamilies
                .FirstOrDefault(f => f.FamilyNames.Values.Any(
                    n => string.Equals(n, typeface, StringComparison.OrdinalIgnoreCase)));
            if (family != null) return true;
        }
        catch { }

        return false;
    }

    private static bool TryFindFile(string fileName, FontDetectionContext context, FindFileHint hint)
    {
        var cacheKey = string.Concat(((int)hint).ToString(), ":", fileName);
        if (context.FindFileCache.TryGetValue(cacheKey, out var cached)) return cached;
        bool found;
        try { var r = HostApplicationServices.Current.FindFile(fileName, context.Db, hint); found = !string.IsNullOrEmpty(r); }
        catch { found = false; }
        context.FindFileCache.TryAdd(cacheKey, found);
        return found;
    }

    private static string? TryFindFilePath(string fileName, FontDetectionContext context, FindFileHint hint)
    {
        try
        {
            string normalized = NormalizeFontName(fileName);
            string result = HostApplicationServices.Current.FindFile(normalized, context.Db, hint);
            if (!string.IsNullOrEmpty(result)) return result;
            if (!Path.HasExtension(normalized))
            {
                result = HostApplicationServices.Current.FindFile(normalized + ".shx", context.Db, hint);
                if (!string.IsNullOrEmpty(result)) return result;
            }
        }
        catch { }
        return null;
    }

    internal static bool IsShxTypeMismatch(string fileName, FontDetectionContext context, bool expectBigFont)
    {
        string? filePath = TryFindFilePath(fileName, context, FindFileHint.CompiledShapeFile);
        if (filePath == null) return false;

        bool? classified = ClassifyShxFile(filePath, context);
        // 分类失败 → 保守处理为不匹配，触发替换修复而非放行
        if (!classified.HasValue) return true;

        return expectBigFont != classified.Value;
    }

    private static bool? ClassifyShxFile(string filePath, FontDetectionContext context)
    {
        if (context.ShxTypeCache.TryGetValue(filePath, out bool cached)) return cached;
        bool isBigFont;
        try
        {
            byte[] header = new byte[30];
            using var fs = File.OpenRead(filePath);
            int bytesRead = fs.Read(header, 0, 30);
            isBigFont = bytesRead >= 25 && System.Text.Encoding.ASCII.GetString(header, 0, bytesRead).Contains("bigfont", StringComparison.OrdinalIgnoreCase);
        }
        catch { return null; }
        context.ShxTypeCache.TryAdd(filePath, isBigFont);
        return isBigFont;
    }

    private static string NormalizeFontName(string name) => Path.GetFileName(name.Trim());

    private static HashSet<string> BuildSystemFontIndex()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                names.Add(family.Source);
                foreach (var localizedName in family.FamilyNames.Values) names.Add(localizedName);
            }
        }
        catch (Exception ex) { LogService.Instance.Error("系统字体索引构建失败", ex); }
        return names;
    }

    #region TrueType 字体特征查询

    public static (int CharacterSet, int PitchAndFamily) GetTrueTypeFontMetrics(string fontName, FontDetectionContext context)
    {
        if (string.IsNullOrEmpty(fontName)) return (0, 0);
        if (context.FontMetricsCache.TryGetValue(fontName, out var cached)) return cached;
        var result = QueryFontMetricsFromGdi(fontName);
        if (result.CharacterSet == 0 && result.PitchAndFamily == 0)
        {
            LogService.Instance.Warning($"[FontMetrics] '{fontName}' GDI 查询失败，返回默认值 (0,0)，未缓存");
            return result;
        }
        context.FontMetricsCache.TryAdd(fontName, result);
        LogService.Instance.Info($"[FontMetrics] '{fontName}' CharSet={result.CharacterSet} Pitch={result.PitchAndFamily}");
        return result;
    }

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