using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.FontMapping;

/// <summary>
/// 单条内联字体修复记录。
/// </summary>
internal sealed record InlineFontFixRecord(
    string MissingFont,
    string ReplacementFont,
    string FixMethod,      // "FMP映射"
    string FontCategory);  // "SHX主字体" / "SHX大字体" / "TrueType"

/// <summary>
/// 字体映射服务 — 通过写入 acad.fmp 文件处理 MText 内联字体缺失。
///
/// FontReplacer 通过修改 TextStyleTableRecord 替换样式表中的缺失字体，
/// 但 MText 内联字体码（\Fgbenor,@gbcbig|c134;）绕过了样式表，
/// 直接按文件名加载字体，FontReplacer 无法修复。
///
/// 本服务将缺失字体的映射写入 acad.fmp，AutoCAD 在解析 DWG 时
/// 会读取此文件进行字体替换，从而避免乱码。映射持久化到文件，
/// 后续打开相同类型图纸时无需再次处理。
///
/// 映射规则（三阶段）：
///
/// 阶段 1 — 内联 SHX 字体（无 @ 前缀）：
///   缺失的主字体 → 映射到 ConfigService.MainFont
///   缺失的大字体 → 映射到 ConfigService.BigFont
///
/// 阶段 2 — 内联 @前缀竖排大字体（如 @gbcbig）：
///   @xxx 缺失 + xxx 存在（原始或阶段1已映射）→ 映射 @xxx → xxx
///   @xxx 缺失 + xxx 不存在 → 映射 @xxx → ConfigService.BigFont
///
/// 阶段 3 — 内联 TrueType 字体：
///   缺失的 TTF 主字体 → 映射到 ConfigService.BigFont
/// </summary>
internal static partial class FontMappingService
{
    private const string FmpMarkerBegin = "AFR_MAP_BEGIN;AFR_MAP_BEGIN";
    private const string FmpMarkerEnd = "AFR_MAP_END;AFR_MAP_END";

    private static string? _fmpPath;

    /// <summary>
    /// 匹配 MText 内联字体码: \Fmain,big|... 或 \Fmain|...
    /// </summary>
    [GeneratedRegex(@"\\F([^,|;]+)(?:,([^|;]+))?\|")]
    private static partial Regex InlineFontRegex();

    /// <summary>
    /// 在 PluginEntry.Initialize() 中调用，定位 acad.fmp 路径。
    /// 此时 FMP 中已有上次会话写入的映射，无需额外操作。
    /// </summary>
    internal static void InitializeFmpPath()
    {
        _fmpPath = FindFmpPath();
        if (_fmpPath != null)
            LogService.Instance.Info($"FontMapping: FMP 路径已定位");
        else
            LogService.Instance.Warning("FontMapping: 未找到 acad.fmp，内联字体映射不可用");
    }

    /// <summary>
    /// 扫描文档中所有 MText 内联字体引用，将缺失字体的映射写入 acad.fmp。
    /// 必须在 FontDetector / FontReplacer 之前调用。
    /// 返回本次新增映射记录列表，供 AFRLOG 界面显示。
    /// </summary>
    internal static List<InlineFontFixRecord> EnsureMissingFonts(Database db)
    {
        var records = new List<InlineFontFixRecord>();

        if (_fmpPath == null)
            return records;

        var log = LogService.Instance;
        var config = ConfigService.Instance;

        string? repMain = NullIfEmpty(config.MainFont);
        string? repBig = NullIfEmpty(config.BigFont);

        if (repMain == null && repBig == null)
            return records;

        // 收集 MText 内联字体引用
        var mainFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bigFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ttfFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFontReferences(db, mainFonts, bigFonts, ttfFonts);

        if (mainFonts.Count == 0 && bigFonts.Count == 0 && ttfFonts.Count == 0)
            return records;

        // 读取现有 FMP 中 AFR 映射，避免重复添加
        var existingMappings = ReadAfrMappings();

        // 计算需要新增的映射
        var newMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 阶段 1 追踪：记录已映射的非 @ 大字体（供阶段 2 使用）
        var mappedBigFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── 阶段 1：常规 SHX 字体（无 @ 前缀）──

        if (repMain != null)
        {
            string repMainShx = EnsureShx(repMain);
            foreach (string font in mainFonts)
            {
                string shx = EnsureShx(font);
                if (string.IsNullOrEmpty(FindFontFile(shx, db)) && !existingMappings.ContainsKey(shx))
                {
                    newMappings.TryAdd(shx, repMainShx);
                    records.Add(new(shx, repMainShx, "FMP映射", "SHX主字体"));
                }
            }
        }

        if (repBig != null)
        {
            string repBigShx = EnsureShx(repBig);
            foreach (string font in bigFonts)
            {
                if (font.StartsWith('@')) continue;

                string shx = EnsureShx(font);
                if (string.IsNullOrEmpty(FindFontFile(shx, db)) && !existingMappings.ContainsKey(shx))
                {
                    newMappings.TryAdd(shx, repBigShx);
                    mappedBigFonts.Add(shx);
                    records.Add(new(shx, repBigShx, "FMP映射", "SHX大字体"));
                }
            }
        }

        // ── 阶段 2：@前缀竖排大字体 ──

        foreach (string font in bigFonts)
        {
            if (!font.StartsWith('@')) continue;

            string shx = EnsureShx(font);
            if (!string.IsNullOrEmpty(FindFontFile(shx, db)) || existingMappings.ContainsKey(shx))
                continue;

            // 优先使用基础字体（去掉 @ 后的同名字体）
            string baseShx = EnsureShx(font[1..]);
            bool baseExists = !string.IsNullOrEmpty(FindFontFile(baseShx, db));

            // 基础字体可能在阶段 1 中刚被映射（虽然文件不存在，但已有替代）
            if (!baseExists && (newMappings.ContainsKey(baseShx) || existingMappings.ContainsKey(baseShx)))
                baseExists = true;

            if (baseExists)
            {
                // 基础字体可用（原始或已映射）→ @xxx 映射到 xxx
                string target = newMappings.TryGetValue(baseShx, out string? mapped) ? mapped
                    : existingMappings.TryGetValue(baseShx, out string? existing) ? existing
                    : baseShx;
                newMappings.TryAdd(shx, target);
                records.Add(new(shx, target, "FMP映射", "SHX大字体"));
            }
            else if (repBig != null)
            {
                string repBigShx = EnsureShx(repBig);
                newMappings.TryAdd(shx, repBigShx);
                records.Add(new(shx, repBigShx, "FMP映射", "SHX大字体"));
            }
        }

        // ── 阶段 3：MText 内联 TrueType 字体 ──

        if (repBig != null)
        {
            string repBigShx = EnsureShx(repBig);
            foreach (string ttf in ttfFonts)
            {
                if (!string.IsNullOrEmpty(FindTtfFile(ttf, db)) || existingMappings.ContainsKey(ttf))
                    continue;

                newMappings.TryAdd(ttf, repBigShx);
                records.Add(new(ttf, repBigShx, "FMP映射", "TrueType"));
            }
        }

        // 写入 FMP 文件
        if (newMappings.Count > 0)
        {
            WriteAfrMappings(existingMappings, newMappings);
            log.Info($"FontMapping: 已写入 {newMappings.Count} 条映射到 acad.fmp");
        }

        return records;
    }

    #region FMP 文件管理

    /// <summary>
    /// 定位 acad.fmp 文件路径。
    /// </summary>
    private static string? FindFmpPath()
    {
        // 方式 1：通过 HostApplicationServices.FindFile
        try
        {
            string path = HostApplicationServices.Current.FindFile(
                "acad.fmp", null, FindFileHint.Default);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;
        }
        catch { }

        // 方式 2：搜索用户 AppData 下的 AutoCAD Support 目录
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string acadBase = Path.Combine(appData, "Autodesk");
            if (Directory.Exists(acadBase))
            {
                foreach (string dir in Directory.GetDirectories(acadBase, "AutoCAD *"))
                {
                    foreach (string fmp in Directory.GetFiles(dir, "acad.fmp", SearchOption.AllDirectories))
                        return fmp;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 读取 FMP 文件中已有的 AFR 映射。
    /// </summary>
    private static Dictionary<string, string> ReadAfrMappings()
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_fmpPath == null || !File.Exists(_fmpPath)) return mappings;

        try
        {
            bool inAfrSection = false;
            foreach (string line in File.ReadAllLines(_fmpPath, Encoding.UTF8))
            {
                if (line.Equals(FmpMarkerBegin, StringComparison.OrdinalIgnoreCase))
                { inAfrSection = true; continue; }

                if (line.Equals(FmpMarkerEnd, StringComparison.OrdinalIgnoreCase))
                { inAfrSection = false; continue; }

                if (inAfrSection)
                {
                    int sep = line.IndexOf(';');
                    if (sep > 0)
                        mappings.TryAdd(line[..sep], line[(sep + 1)..]);
                }
            }
        }
        catch { }

        return mappings;
    }

    /// <summary>
    /// 将 AFR 映射写入 FMP 文件（合并已有 + 新增，保留系统映射）。
    /// </summary>
    private static void WriteAfrMappings(
        Dictionary<string, string> existing, Dictionary<string, string> newMappings)
    {
        if (_fmpPath == null) return;

        try
        {
            // 读取系统映射（AFR 区段之外的行）
            var systemLines = new List<string>();
            if (File.Exists(_fmpPath))
            {
                bool inAfrSection = false;
                foreach (string line in File.ReadAllLines(_fmpPath, Encoding.UTF8))
                {
                    if (line.Equals(FmpMarkerBegin, StringComparison.OrdinalIgnoreCase))
                    { inAfrSection = true; continue; }

                    if (line.Equals(FmpMarkerEnd, StringComparison.OrdinalIgnoreCase))
                    { inAfrSection = false; continue; }

                    if (!inAfrSection)
                        systemLines.Add(line);
                }
            }

            // 合并已有 + 新增映射
            var allMappings = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in newMappings)
                allMappings[key] = value;

            // 写回文件
            using var writer = new StreamWriter(_fmpPath, false, Encoding.UTF8);
            foreach (string line in systemLines)
                writer.WriteLine(line);

            if (allMappings.Count > 0)
            {
                writer.WriteLine(FmpMarkerBegin);
                foreach (var (source, target) in allMappings)
                    writer.WriteLine($"{source};{target}");
                writer.WriteLine(FmpMarkerEnd);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error("FontMapping: 写入 acad.fmp 失败", ex);
        }
    }

    #endregion

    #region 字体引用收集

    /// <summary>
    /// 仅从 MText 内联字体码中收集字体引用。
    /// SHX 主字体 → mainFonts, 大字体 → bigFonts, TrueType → ttfFonts
    /// </summary>
    private static void CollectFontReferences(Database db,
        HashSet<string> mainFonts, HashSet<string> bigFonts, HashSet<string> ttfFonts)
    {
        try
        {
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                foreach (ObjectId entId in btr)
                {
                    if (!entId.ObjectClass.DxfName.Equals("MTEXT", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var mtext = (MText)tr.GetObject(entId, OpenMode.ForRead);
                    string? contents = mtext.Contents;
                    if (string.IsNullOrEmpty(contents)) continue;

                    foreach (Match match in InlineFontRegex().Matches(contents))
                    {
                        string main = match.Groups[1].Value;
                        string big = match.Groups[2].Value;

                        if (IsTrueTypeName(main))
                            ttfFonts.Add(main);
                        else if (IsShxName(main))
                            mainFonts.Add(main);

                        if (!string.IsNullOrEmpty(big))
                            bigFonts.Add(big);
                    }
                }
            }

            tr.Commit();
        }
        catch { }
    }

    #endregion

    #region 辅助方法

    private static string FindFontFile(string fileName, Database db)
    {
        try
        {
            string result = HostApplicationServices.Current.FindFile(
                fileName, db, FindFileHint.CompiledShapeFile);
            return string.IsNullOrEmpty(result) ? string.Empty : result;
        }
        catch { return string.Empty; }
    }

    private static string FindTtfFile(string fileName, Database db)
    {
        try
        {
            string result = HostApplicationServices.Current.FindFile(
                fileName, db, FindFileHint.TrueTypeFontFile);
            return string.IsNullOrEmpty(result) ? string.Empty : result;
        }
        catch { return string.Empty; }
    }

    private static bool IsShxName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return !IsTrueTypeName(name);
    }

    private static bool IsTrueTypeName(string name) =>
        name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    private static string EnsureShx(string name) =>
        name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ? name : name + ".shx";

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    #endregion
}
