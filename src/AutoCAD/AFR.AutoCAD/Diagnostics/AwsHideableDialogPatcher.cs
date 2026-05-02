using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using AFR.Platform;
using AFR.Services;

namespace AFR.Diagnostics;

/// <summary>
/// 抑制 AutoCAD 2018+ 引入的"缺少 SHX 文件"对话框（id=Acad.UnresolvedFontFiles）。
/// <para>
/// 该对话框的勾选状态持久化在 <c>%APPDATA%\Autodesk\AutoCAD <year>\R*\<lang>\Support\Profiles\FixedProfile.aws</c>，
/// 路径 <c>Profile/StorageRoot/AcApData/HideableDialogs/HideableDialog</c>。
/// 注册表层面无对应键，因此抑制必须直接写入该 .aws 文件。
/// </para>
/// <para>
/// <see cref="Apply"/> 写入带 AFR 自有标记（<c>afrToken</c> 属性）的节点，<see cref="Cleanup"/> 仅删除
/// 带该标记的节点，因此用户手动设置过的同名节点不会被误删。
/// </para>
/// <para>
/// 写入时机要求：AutoCAD 进程必须处于未运行状态——AutoCAD 退出会用内存快照覆写 <c>FixedProfile.aws</c>，
/// 因此运行中写入的内容会被立刻丢弃。<see cref="Apply"/> / <see cref="Cleanup"/> 在检测到 acad.exe
/// 运行时会直接放弃操作并返回 false。
/// </para>
/// <para>
/// 仅作用于"已部署本插件"的 CAD 版本（由 <see cref="PlatformManager.Platform"/> 决定的 R&lt;version&gt;
/// 目录）+ 该版本下全部语言目录；不跨版本扫描。
/// </para>
/// </summary>
public static class AwsHideableDialogPatcher
{
    /// <summary>抑制目标对话框的固定 id。</summary>
    private const string DialogId = "Acad.UnresolvedFontFiles";
    /// <summary>所有权标记属性名 — 仅由本插件写入，<see cref="Cleanup"/> 据此识别。</summary>
    private const string OwnershipAttr = "afrToken";
    /// <summary>所有权标记取值 — 与具体写入时间无关，仅作识别。</summary>
    private const string OwnershipToken = "AFR.AwsHideableDialogPatcher";
    /// <summary>持久化文件名。</summary>
    private const string FixedProfileFileName = "FixedProfile.aws";
    /// <summary>对话框元数据 — 与 AutoCAD 自写出的节点结构一致。</summary>
    private const string DialogTitle = "缺少 SHX 文件";
    /// <summary>勾选"不再显示"后 AutoCAD 写出的固定结果码（继续打开图纸）。</summary>
    private const string DialogResult = "1002";
    /// <summary>TaskDialog 子节点的 XAML 资源引用 — 与 AutoCAD 自写一致。</summary>
    private const string TaskDialogSource = "/AcTaskDialogs;component/TaskDialogs.xaml";
    /// <summary>TaskDialog 子节点的命名空间 — 与 AutoCAD 自写一致。</summary>
    private const string TaskDialogXmlns = "clr-namespace:Autodesk.Windows;assembly=AdWindows";

    /// <summary>
    /// 对当前插件版本对应的所有 .aws 文件写入抑制节点。
    /// AutoCAD 运行中、文件不存在或写入异常均视为本次跳过。
    /// </summary>
    /// <returns>实际写入或刷新的文件数量。</returns>
    public static int Apply()
    {
        if (IsAutoCadRunning())
        {
            DiagnosticLogger.Log("AwsPatcher", "Apply 跳过：检测到 acad.exe 运行中。");
            return 0;
        }

        int count = 0;
        foreach (var path in EnumerateTargetAwsFiles())
        {
            try
            {
                if (WriteOwnedNode(path)) count++;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("AwsPatcher", $"写入 {path} 失败：{ex.Message}");
            }
        }
        return count;
    }

    /// <summary>
    /// 删除当前插件版本对应的所有 .aws 文件中带 AFR 所有权标记的抑制节点。
    /// 用户手动设置（无标记）的同名节点不会被删除。
    /// AutoCAD 运行中、文件不存在或写入异常均视为本次跳过。
    /// </summary>
    /// <returns>实际清理的文件数量。</returns>
    public static int Cleanup()
    {
        if (IsAutoCadRunning())
        {
            DiagnosticLogger.Log("AwsPatcher", "Cleanup 跳过：检测到 acad.exe 运行中。");
            return 0;
        }

        int count = 0;
        foreach (var path in EnumerateTargetAwsFiles())
        {
            try
            {
                if (RemoveOwnedNode(path)) count++;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("AwsPatcher", $"清理 {path} 失败：{ex.Message}");
            }
        }
        return count;
    }

    /// <summary>枚举本插件版本对应的所有 <c>FixedProfile.aws</c> 候选路径（诊断用）。</summary>
    public static string[] ListTargetAwsFiles() => EnumerateTargetAwsFiles().ToArray();

    /// <summary>定位活动 <c>FixedProfile.aws</c>：候选中最近修改的一个；无候选返回 null。</summary>
    public static string? LocateActiveAwsPath()
    {
        var all = EnumerateTargetAwsFiles().ToArray();
        if (all.Length == 0) return null;
        return all.OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc).First();
    }

    /// <summary>读取指定 .aws 中 HideableDialog[id=Acad.UnresolvedFontFiles] 节点 XML；不存在返回空串。</summary>
    public static string ReadDialogNodeXml(string awsPath)
    {
        if (!File.Exists(awsPath)) return string.Empty;
        var doc = XDocument.Load(awsPath);
        var node = doc.Descendants()
                      .FirstOrDefault(e => e.Name.LocalName == "HideableDialog"
                                        && (string?)e.Attribute("id") == DialogId);
        return node?.ToString(SaveOptions.DisableFormatting) ?? string.Empty;
    }

    /// <summary>判断 acad.exe 是否在运行。任何异常一律视为"在运行"，从安全侧拒绝写入。</summary>
    private static bool IsAutoCadRunning()
    {
        try { return Process.GetProcessesByName("acad").Length > 0; }
        catch { return true; }
    }

    /// <summary>
    /// 枚举当前插件版本对应的所有 <c>FixedProfile.aws</c> 候选路径。
    /// 形如 <c>%APPDATA%\Autodesk\AutoCAD&lt;year&gt;\R&lt;ver&gt;\&lt;lang&gt;\Support\Profiles\FixedProfile.aws</c>，
    /// 其中 <c>R&lt;ver&gt;</c> 取自 <see cref="ICadPlatform.RegistryBasePath"/> 末段，仅扫描该版本目录下的所有语言子目录。
    /// </summary>
    private static IEnumerable<string> EnumerateTargetAwsFiles()
    {
        var versionTag = ExtractVersionTag(PlatformManager.Platform.RegistryBasePath);
        if (string.IsNullOrEmpty(versionTag)) yield break;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var autodeskRoot = Path.Combine(appData, "Autodesk");
        if (!Directory.Exists(autodeskRoot)) yield break;

        IEnumerable<string> productDirs;
        try { productDirs = Directory.EnumerateDirectories(autodeskRoot, "AutoCAD*", SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var productDir in productDirs)
        {
            var versionDir = Path.Combine(productDir, versionTag);
            if (!Directory.Exists(versionDir)) continue;

            IEnumerable<string> langDirs;
            try { langDirs = Directory.EnumerateDirectories(versionDir, "*", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (var langDir in langDirs)
            {
                var profilesDir = Path.Combine(langDir, "Support", "Profiles");
                if (!Directory.Exists(profilesDir)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(profilesDir, FixedProfileFileName, SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var f in files) yield return f;
            }
        }
    }

    /// <summary>从注册表基路径（如 <c>Software\Autodesk\AutoCAD\R25.0</c>）提取末段 <c>R25.0</c>。</summary>
    private static string ExtractVersionTag(string registryBasePath)
    {
        if (string.IsNullOrEmpty(registryBasePath)) return string.Empty;
        var idx = registryBasePath.LastIndexOf('\\');
        return idx >= 0 ? registryBasePath[(idx + 1)..] : registryBasePath;
    }

    /// <summary>
    /// 写入或刷新带 AFR 标记的 HideableDialog 节点。返回 true 表示文件被修改。
    /// 已存在带相同标记且结构一致的节点时不修改文件。
    /// </summary>
    private static bool WriteOwnedNode(string path)
    {
        if (!File.Exists(path)) return false;

        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var profile = doc.Root;
        if (profile is null || profile.Name.LocalName != "Profile") return false;
        var ns = profile.GetDefaultNamespace();

        var storageRoot = profile.Element(ns + "StorageRoot") ?? AddChild(profile, ns + "StorageRoot");
        var acApData    = storageRoot.Element(ns + "AcApData") ?? AddChild(storageRoot, ns + "AcApData");
        var hideables   = acApData.Element(ns + "HideableDialogs") ?? AddChild(acApData, ns + "HideableDialogs");

        // 保留用户手动设置的同名节点（无 AFR 标记）；只重写自身节点。
        var existing = hideables.Elements(ns + "HideableDialog")
                                .FirstOrDefault(e => (string?)e.Attribute("id") == DialogId);
        if (existing is not null && (string?)existing.Attribute(OwnershipAttr) != OwnershipToken)
        {
            // 用户预设节点存在 — 保留不动。
            return false;
        }
        existing?.Remove();

        XNamespace adWin = TaskDialogXmlns;
        var taskDialog = new XElement(adWin + "TaskDialog",
            new XAttribute("Source", TaskDialogSource),
            new XAttribute("Id", DialogId));
        var preview = new XElement(ns + "Preview", taskDialog);

        var node = new XElement(ns + "HideableDialog",
            new XAttribute("id", DialogId),
            new XAttribute("title", DialogTitle),
            new XAttribute("category", DialogTitle),
            new XAttribute("application", ""),
            new XAttribute("result", DialogResult),
            new XAttribute(OwnershipAttr, OwnershipToken),
            preview);
        hideables.Add(node);

        SaveAtomically(doc, path);
        return true;
    }

    /// <summary>删除带 AFR 标记的 HideableDialog 节点。返回 true 表示文件被修改。</summary>
    private static bool RemoveOwnedNode(string path)
    {
        if (!File.Exists(path)) return false;

        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var profile = doc.Root;
        if (profile is null || profile.Name.LocalName != "Profile") return false;

        var owned = profile.Descendants()
                           .Where(e => e.Name.LocalName == "HideableDialog"
                                    && (string?)e.Attribute("id") == DialogId
                                    && (string?)e.Attribute(OwnershipAttr) == OwnershipToken)
                           .ToList();
        if (owned.Count == 0) return false;

        foreach (var n in owned) n.Remove();
        SaveAtomically(doc, path);
        return true;
    }

    /// <summary>原子方式保存 XML：先写临时文件再替换，避免崩溃留下半截 .aws。</summary>
    private static void SaveAtomically(XDocument doc, string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, FixedProfileFileName + ".afr-tmp");
        // AutoCAD 自写出的 .aws 是 UTF-8 BOM；这里保留 BOM 以避免兼容差异。
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
        {
            doc.Save(writer, SaveOptions.DisableFormatting);
        }
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* 残留临时文件不影响主流程 */ }
    }

    private static XElement AddChild(XElement parent, XName name)
    {
        var c = new XElement(name);
        parent.Add(c);
        return c;
    }
}
