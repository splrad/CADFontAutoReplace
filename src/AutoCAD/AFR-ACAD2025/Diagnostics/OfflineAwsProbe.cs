#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace AFR.Diagnostics;

/// <summary>
/// 仅 DEBUG：被外部进程（PowerShell + Assembly.LoadFrom）通过反射调用，
/// 在 AutoCAD 未运行时对 *Fixed_Profile.aws 做最小化探针写入 / 读取 / 还原。
/// <para>所有公开 API 只接受 / 返回原生类型，便于 PowerShell 反射调用。</para>
/// <para>仅作用于本 DLL 对应版本（R25.0）+ 当前语言；不扫描其它版本目录。</para>
/// </summary>
public static class OfflineAwsProbe
{
    private const string TargetVersion = "R25.0"; // 与 AutoCad2025Platform.RegistryBasePath 末段一致
    private const string FixedProfileFileName = "FixedProfile.aws";
    private const string DialogId = "Acad.UnresolvedFontFiles";

    /// <summary>
    /// 列出 %APPDATA%\Autodesk\AutoCAD*\R*\*\Support\Profiles\*Fixed_Profile.aws 的全部候选。
    /// 用于诊断"为什么 LocateActiveAwsPath 返回 null"。
    /// </summary>
    public static string[] ListAllAwsCandidates()
    {
        var result = new System.Collections.Generic.List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var autodeskRoot = Path.Combine(appData, "Autodesk");
        if (!Directory.Exists(autodeskRoot)) return result.ToArray();

        foreach (var productDir in Safe(() => Directory.EnumerateDirectories(autodeskRoot, "AutoCAD*", SearchOption.TopDirectoryOnly)))
            foreach (var verDir in Safe(() => Directory.EnumerateDirectories(productDir, "R*", SearchOption.TopDirectoryOnly)))
                foreach (var langDir in Safe(() => Directory.EnumerateDirectories(verDir, "*", SearchOption.TopDirectoryOnly)))
                {
                    var profilesDir = Path.Combine(langDir, "Support", "Profiles");
                    if (!Directory.Exists(profilesDir)) continue;
                    foreach (var f in Safe(() => Directory.EnumerateFiles(profilesDir, FixedProfileFileName, SearchOption.AllDirectories)))
                        result.Add(f);
                }
        return result.ToArray();
    }

    /// <summary>
    /// 定位 *Fixed_Profile.aws 路径。优先匹配 R25.0；找不到则取所有候选中最近修改的一个。
    /// 全部候选均不存在时返回 null。
    /// </summary>
    public static string? LocateActiveAwsPath()
    {
        var all = ListAllAwsCandidates();
        if (all.Length == 0) return null;

        // 1) 优先 R25.0
        var preferred = all.Where(p => p.IndexOf(@"\" + TargetVersion + @"\", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        var pool = preferred.Length > 0 ? preferred : all;

        // 2) 同 pool 内，优先 chs/enu
        var lang = pool.Where(p => p.IndexOf(@"\chs\", StringComparison.OrdinalIgnoreCase) >= 0
                                || p.IndexOf(@"\enu\", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        if (lang.Length > 0) pool = lang;

        // 3) 取最近修改
        return pool.OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc).First();
    }

    private static System.Collections.Generic.IEnumerable<string> Safe(Func<System.Collections.Generic.IEnumerable<string>> f)
    {
        try { return f(); } catch { return Array.Empty<string>(); }
    }

    /// <summary>读取当前 .aws 全文（UTF-8）。</summary>
    public static string ReadAwsContent()
    {
        var path = LocateActiveAwsPath() ?? throw new FileNotFoundException("aws not located");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>是否包含给定 token（写入时附加在自定义属性 afrToken 上）。</summary>
    public static bool ContainsToken(string token)
    {
        var path = LocateActiveAwsPath();
        if (path == null || !File.Exists(path)) return false;
        return File.ReadAllText(path, Encoding.UTF8).Contains($"afrToken=\"{token}\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// 写入完整 HideableDialog 节点（含 Preview/TaskDialog 子树），路径：
    /// <c>Profile/StorageRoot/AcApData/HideableDialogs/HideableDialog</c>。
    /// 与 AutoCAD 自身写出的真实节点结构一致。可选 token 作为 <c>afrToken</c> 属性挂在节点上以便取证。
    /// 调用方必须确保 AutoCAD 已退出。
    /// </summary>
    public static string WriteMinimalNode(string? token)
    {
        var path = LocateActiveAwsPath() ?? throw new FileNotFoundException("aws not located");
        var backup = path + ".afr-backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        File.Copy(path, backup, overwrite: true);

        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var profile = doc.Root ?? throw new InvalidDataException("missing root <Profile>");
        var ns = profile.GetDefaultNamespace(); // 多数情况下为 XNamespace.None

        // 真实路径：Profile / StorageRoot / AcApData / HideableDialogs / HideableDialog
        var storageRoot = profile.Element(ns + "StorageRoot") ?? AddChild(profile, ns + "StorageRoot");
        var acApData    = storageRoot.Element(ns + "AcApData") ?? AddChild(storageRoot, ns + "AcApData");
        var hideables   = acApData.Element(ns + "HideableDialogs") ?? AddChild(acApData, ns + "HideableDialogs");

        // 移除已有同 id 节点
        hideables.Elements(ns + "HideableDialog")
                 .Where(e => (string?)e.Attribute("id") == DialogId)
                 .Remove();

        // 构造与 AutoCAD 自写完全一致的 Preview/TaskDialog 子树
        // 注意 TaskDialog 自带命名空间 xmlns="clr-namespace:Autodesk.Windows;assembly=AdWindows"
        XNamespace adWin = "clr-namespace:Autodesk.Windows;assembly=AdWindows";
        var taskDialog = new XElement(adWin + "TaskDialog",
            new XAttribute("Source", "/AcTaskDialogs;component/TaskDialogs.xaml"),
            new XAttribute("Id", DialogId));
        var preview = new XElement(ns + "Preview", taskDialog);

        var node = new XElement(ns + "HideableDialog",
            new XAttribute("id", DialogId),
            new XAttribute("title", "缺少 SHX 文件"),
            new XAttribute("category", "缺少 SHX 文件"),
            new XAttribute("application", ""),
            new XAttribute("result", "1002"),
            preview);
        if (!string.IsNullOrEmpty(token))
            node.SetAttributeValue("afrToken", token);
        hideables.Add(node);

        // 不写 XML 声明的 standalone，使用与原文件兼容的格式
        doc.Save(path, SaveOptions.DisableFormatting);
        return backup;
    }

    /// <summary>恢复指定备份。</summary>
    public static void RestoreBackup(string backupPath)
    {
        var path = LocateActiveAwsPath() ?? throw new FileNotFoundException("aws not located");
        File.Copy(backupPath, path, overwrite: true);
    }

    /// <summary>提取当前 .aws 中 HideableDialog[id=Acad.UnresolvedFontFiles] 的 OuterXml；不存在返回空串。</summary>
    public static string ReadDialogNodeXml()
    {
        var path = LocateActiveAwsPath();
        if (path == null || !File.Exists(path)) return "";
        var doc = XDocument.Load(path);
        var node = doc.Descendants()
                      .FirstOrDefault(e => e.Name.LocalName == "HideableDialog"
                                        && (string?)e.Attribute("id") == DialogId);
        return node?.ToString(SaveOptions.DisableFormatting) ?? "";
    }

    private static XElement AddChild(XElement parent, XName name)
    {
        var c = new XElement(name);
        parent.Add(c);
        return c;
    }
}
#endif
