using System.Collections.Concurrent;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using System.Windows.Media;

namespace AFR_ACAD2026.Services;

/// <summary>
/// 单个文字样式的字体可用性检查结果。
/// </summary>
internal sealed record FontCheckResult(
    string StyleName,
    string FileName,
    string BigFontFileName,
    bool IsMainFontMissing,
    bool IsBigFontMissing,
    bool IsTrueType);

/// <summary>
/// 检测图纸 TextStyleTable 中的缺失字体。
/// 支持 TrueType（系统字体 + CAD 搜索路径）、SHX（FindFile）、大字体（FindFile）。
/// 通过名称归一化处理 acad.fmp 字体映射问题。
/// FindFile 结果会话级缓存，避免重复磁盘 I/O。
/// </summary>
internal static class FontDetector
{
    private static readonly Lazy<HashSet<string>> _systemFontNames = new(BuildSystemFontIndex);

    // FindFile 结果缓存 — 字体可用性在 CAD 会话期间不变
    // Key: "{hint}:{normalizedFileName}" Value: 是否找到
    private static readonly ConcurrentDictionary<string, bool> _findFileCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 扫描数据库中所有文字样式，返回存在缺失字体的样式列表。
    /// </summary>
    public static List<FontCheckResult> DetectMissingFonts(Database db)
    {
        var results = new List<FontCheckResult>();

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

                bool isTrueType = !string.IsNullOrEmpty(font.TypeFace);
                bool isMainMissing = false;
                bool isBigMissing = false;

                if (isTrueType)
                {
                    isMainMissing = !IsTrueTypeFontAvailable(font.TypeFace, fileName, db);
                }
                else if (!string.IsNullOrWhiteSpace(fileName))
                {
                    isMainMissing = !IsShxFontAvailable(fileName, db);
                }

                if (!string.IsNullOrWhiteSpace(bigFontName))
                {
                    isBigMissing = !IsShxFontAvailable(bigFontName, db);
                }

                if (isMainMissing || isBigMissing)
                {
                    results.Add(new FontCheckResult(
                        styleName, fileName, bigFontName,
                        isMainMissing, isBigMissing, isTrueType));
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
    /// 检查 SHX 字体文件是否可被 AutoCAD 找到。
    /// 通过名称归一化处理 acad.fmp 兼容性。
    /// </summary>
    private static bool IsShxFontAvailable(string fileName, Database db)
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
    /// 第三方插件字体可能位于 CAD 支持路径而非系统目录。
    /// </summary>
    private static bool IsTrueTypeFontAvailable(string typeface, string fileName, Database db)
    {
        if (string.IsNullOrWhiteSpace(typeface)) return true;

        // 检查系统已安装字体（包含本地化名称）
        if (_systemFontNames.Value.Contains(typeface))
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

        _findFileCache.TryAdd(cacheKey, found);
        return found;
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
        catch
        {
            // 系统字体枚举失败 — 安全降级
        }
        return names;
    }
}
