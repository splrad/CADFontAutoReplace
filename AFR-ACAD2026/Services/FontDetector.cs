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
    bool IsTrueType,
    string TypeFace);

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
        return task.IsCompleted && task.Result.Contains(name);
    }

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

                // 判断样式类型：SHX 还是 TrueType
                // 规则：TypeFace 非空 且 FileName 不是 SHX 格式 → TrueType
                //       其他情况 → SHX
                // 当 TypeFace 和 FileName 同时有值时（DWG 数据不一致），FileName 优先。
                // 原因：走 TrueType 分支会设置 FontDescriptor(TypeFace, 0, 0)，
                //       AutoCAD 加载时会"修正"为实际 CharacterSet/PitchAndFamily，
                //       导致内部状态与 DWG 不一致，ST 弹出"当前样式已修改"。
                //       走 SHX 分支则清空 TypeFace + 设 FileName，无 TrueType 解析，无此问题。
                bool fileNameIsSHX = !string.IsNullOrWhiteSpace(fileName)
                    && !fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                    && !fileName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
                    && !fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
                bool isTrueType = !string.IsNullOrEmpty(font.TypeFace) && !fileNameIsSHX;
                bool isMainMissing = false;
                bool isBigMissing = false;

                if (isTrueType)
                {
                    isMainMissing = !IsTrueTypeFontAvailable(font.TypeFace, fileName, db);
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
        if (_systemFontNamesTask.Result.Contains(typeface))
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

        bool isBigFont = false;
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
        }
        catch { }

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
        catch
        {
            // 系统字体枚举失败 — 安全降级
        }
        return names;
    }
}
