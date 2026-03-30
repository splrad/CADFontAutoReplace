using System.IO;
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
    string FixMethod,      // "硬链接" 或 "内容替换"
    string FontCategory);  // "SHX主字体" / "SHX大字体" / "TrueType"

/// <summary>
/// 字体映射服务 — 专门处理 FontReplacer 无法覆盖的字体缺失场景。
///
/// FontReplacer 通过修改 TextStyleTableRecord 替换样式表中的缺失字体，
/// 但 MText 内联字体码（\Fgbenor,@gbcbig|c134;）绕过了样式表，
/// 直接按文件名加载字体，FontReplacer 无法修复。
///
/// 本服务仅处理 MText 内联字体码中引用的缺失字体：
///
/// 阶段 1 — 内联 SHX 字体（无 @ 前缀）：
///   缺失的主字体 → 硬链接到 ConfigService.MainFont
///   缺失的大字体 → 硬链接到 ConfigService.BigFont
///
/// 阶段 2 — 内联 @前缀竖排大字体（如 @gbcbig）：
///   @xxx 缺失 + xxx 存在（原文件或阶段1新建）→ 硬链接 @xxx → xxx
///   @xxx 缺失 + xxx 不存在 → 硬链接 @xxx → ConfigService.BigFont
///
/// 阶段 3 — 内联 TrueType 字体：
///   缺失的 TTF 主字体 → 修改 MText.Contents，替换为 ConfigService.MainFont
/// </summary>
internal static partial class FontMappingService
{
    /// <summary>
    /// 匹配 MText 内联字体码: \Fmain,big|... 或 \Fmain|...
    /// </summary>
    [GeneratedRegex(@"\\F([^,|;]+)(?:,([^|;]+))?\|")]
    private static partial Regex InlineFontRegex();

    /// <summary>
    /// 扫描文档中所有 MText 内联字体引用，为缺失的字体创建硬链接或替换内容。
    /// 必须在 FontDetector / FontReplacer 之前调用。
    /// 返回本次修复记录列表，供 AFRLOG 界面显示。
    /// </summary>
    internal static List<InlineFontFixRecord> EnsureMissingFonts(Database db)
    {
        var log = LogService.Instance;
        var config = ConfigService.Instance;
        var records = new List<InlineFontFixRecord>();

        string? repMain = NullIfEmpty(config.MainFont);
        string? repBig = NullIfEmpty(config.BigFont);

        if (repMain == null && repBig == null)
            return records;

        // 查找替换字体的实际文件路径
        string? repMainPath = repMain != null ? FindFontFile(EnsureShx(repMain), db) : null;
        string? repBigPath = repBig != null ? FindFontFile(EnsureShx(repBig), db) : null;

        if (repMainPath == null && repBigPath == null)
            return records;

        // 收集所有 SHX 字体引用
        var mainFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bigFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFontReferences(db, mainFonts, bigFonts);

        if (mainFonts.Count == 0 && bigFonts.Count == 0)
            return records;

        // 记录本次创建的硬链接（文件名 → 完整路径），供阶段2查找阶段1新建的字体
        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── 阶段 1：常规字体（无 @ 前缀）──

        if (repMainPath != null)
        {
            foreach (string font in mainFonts)
            {
                string shx = EnsureShx(font);
                if (string.IsNullOrEmpty(FindFontFile(shx, db)))
                    if (TryCreateLink(shx, repMainPath, created, log))
                        records.Add(new(shx, Path.GetFileName(repMainPath), "硬链接", "SHX主字体"));
            }
        }

        if (repBigPath != null)
        {
            foreach (string font in bigFonts)
            {
                if (font.StartsWith('@')) continue; // 阶段2处理

                string shx = EnsureShx(font);
                if (string.IsNullOrEmpty(FindFontFile(shx, db)))
                    if (TryCreateLink(shx, repBigPath, created, log))
                        records.Add(new(shx, Path.GetFileName(repBigPath), "硬链接", "SHX大字体"));
            }
        }

        // ── 阶段 2：@前缀竖排大字体 ──

        foreach (string font in bigFonts)
        {
            if (!font.StartsWith('@')) continue;

            string shx = EnsureShx(font);
            if (!string.IsNullOrEmpty(FindFontFile(shx, db)))
                continue;

            string baseShx = EnsureShx(font[1..]);
            string? basePath = FindFontFile(baseShx, db);

            if (string.IsNullOrEmpty(basePath) && created.TryGetValue(baseShx, out string? createdPath))
                basePath = createdPath;

            string? targetPath = !string.IsNullOrEmpty(basePath) ? basePath : repBigPath;
            if (targetPath != null && TryCreateLink(shx, targetPath, created, log))
                records.Add(new(shx, Path.GetFileName(targetPath), "硬链接", "SHX大字体"));
        }

        // ── 阶段 3：MText 内联 TrueType 字体替换 ──

        if (repMain != null)
        {
            int ttfCount = ReplaceMissingInlineTtfFonts(db, repMain, log);
            if (ttfCount > 0)
                records.Add(new($"MText 内联 ({ttfCount} 个)", repMain, "内容替换", "TrueType"));
        }

        return records;
    }

    #region 阶段 3：MText 内联 TrueType 替换

    /// <summary>
    /// 扫描所有 MText 实体，将内联字体码中缺失的 TrueType 主字体
    /// 替换为用户配置的 SHX 主字体。仅修改 MText.Contents，不影响样式表。
    ///
    /// 示例：\Ftxt_____.ttf,gbcbig|c134; → \Fgbenor,gbcbig|c134;
    /// </summary>
    private static int ReplaceMissingInlineTtfFonts(Database db, string shxMainFont, LogService log)
    {
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            int replaceCount = 0;

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

                    string modified = InlineFontRegex().Replace(contents, match =>
                    {
                        string mainFont = match.Groups[1].Value;

                        // 仅处理显式 TrueType 字体引用（.ttf/.ttc/.otf）
                        if (!IsTrueTypeName(mainFont))
                            return match.Value;

                        // 字体可用则跳过
                        if (!string.IsNullOrEmpty(FindTtfFile(mainFont, db)))
                            return match.Value;

                        // 替换主字体名，保留大字体和代码页
                        return string.Concat(@"\F", shxMainFont, match.Value.AsSpan(2 + mainFont.Length));
                    });

                    if (!string.Equals(modified, contents, StringComparison.Ordinal))
                    {
                        mtext.UpgradeOpen();
                        mtext.Contents = modified;
                        replaceCount++;
                    }
                }
            }

            tr.Commit();

            if (replaceCount > 0)
                log.Info($"FontMapping: 已替换 {replaceCount} 个 MText 中缺失的内联 TrueType 字体 → {shxMainFont}");

            return replaceCount;
        }
        catch (Exception ex)
        {
            log.Error("FontMapping: MText 内联 TrueType 字体替换失败", ex);
            return 0;
        }
    }

    private static bool IsTrueTypeName(string name) =>
        name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

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

    #endregion

    #region 字体引用收集

    /// <summary>
    /// 仅从 MText 内联字体码中收集 SHX 字体引用。
    /// 样式表中的缺失字体由 FontReplacer 处理，不在此收集。
    /// </summary>
    private static void CollectFontReferences(Database db, HashSet<string> mainFonts, HashSet<string> bigFonts)
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

                        if (IsShxName(main))
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

    /// <summary>
    /// 创建硬链接（回退到拷贝），并记录到 created 字典。
    /// 成功返回 true。
    /// </summary>
    private static bool TryCreateLink(string fileName, string targetPath,
        Dictionary<string, string> created, LogService log)
    {
        string dir = Path.GetDirectoryName(targetPath)!;
        string linkPath = Path.Combine(dir, fileName);

        if (File.Exists(linkPath) || created.ContainsKey(fileName))
            return false;

        try
        {
            if (CreateHardLink(linkPath, targetPath))
                log.Info($"FontMapping: 已创建硬链接 {fileName} → {Path.GetFileName(targetPath)}");
            else
            {
                File.Copy(targetPath, linkPath);
                log.Info($"FontMapping: 已拷贝 {Path.GetFileName(targetPath)} → {fileName}");
            }
            created[fileName] = linkPath;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            log.Warning($"FontMapping: 无权限创建 {fileName}（需要管理员权限写入 Fonts 目录）");
            return false;
        }
        catch (Exception ex)
        {
            log.Error($"FontMapping: 创建 {fileName} 失败", ex);
            return false;
        }
    }

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

    /// <summary>
    /// 判断是否为 SHX 字体名（排除 TrueType 字体）。
    /// </summary>
    private static bool IsShxName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // 排除 TrueType 扩展名
        if (name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string EnsureShx(string name) =>
        name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ? name : name + ".shx";

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes = 0);

    #endregion
}
