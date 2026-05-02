using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using AFR.Deployer.Models;

namespace AFR.Deployer.Services;

/// <summary>
/// 部署器侧的 AutoCAD "缺少 SHX 文件" 对话框抑制器（参数驱动，零运行时单例依赖）。
/// <para>
/// 与 AutoCAD 进程内 <c>AFR.Diagnostics.AwsHideableDialogPatcher</c> 行为一致：
/// 写入 / 删除 <c>%APPDATA%\Autodesk\AutoCAD &lt;year&gt;\R&lt;ver&gt;\&lt;lang&gt;\Support\Profiles\FixedProfile.aws</c>
/// 中带 <c>afrToken</c> 标记的 <c>HideableDialog</c> 节点。
/// 区别在于：
/// <list type="bullet">
///   <item>不依赖 <c>PlatformManager</c>，所有 CAD 元数据由调用方通过 <see cref="CadDescriptor"/> 提供；</item>
///   <item>由 Deployer 在 CAD 已关闭后调用——这是修改 .aws 唯一可靠的时机
///         （AutoCAD 退出会用内存快照覆写本文件）。</item>
/// </list>
/// </para>
/// <para>
/// 触发场景：用户在 AFR 部署工具中"安装/卸载"插件时，安装完成后写入抑制节点；
/// 卸载完成后清理本插件写入的节点（保留用户手动设置的同名节点）。
/// </para>
/// </summary>
internal static class AwsHideableDialogPatcher
{
    private const string DialogId            = "Acad.UnresolvedFontFiles";
    private const string OwnershipAttr       = "afrToken";
    private const string OwnershipToken      = "AFR.AwsHideableDialogPatcher";
    private const string FixedProfileFileName = "FixedProfile.aws";
    private const string DialogTitle         = "缺少 SHX 文件";
    private const string DialogResult        = "1002";
    private const string TaskDialogSource    = "/AcTaskDialogs;component/TaskDialogs.xaml";
    private const string TaskDialogXmlns     = "clr-namespace:Autodesk.Windows;assembly=AdWindows";

    /// <summary>
    /// 对指定 CAD 版本的所有 <c>FixedProfile.aws</c> 写入抑制节点。
    /// AutoCAD 运行中、文件不存在或写入异常一律视为本次跳过（不抛出）。
    /// </summary>
    /// <returns>实际写入或刷新的文件数量。</returns>
    public static int Apply(CadDescriptor descriptor)
    {
        if (IsAutoCadRunning()) return 0;

        int count = 0;
        foreach (var path in EnumerateTargetAwsFiles(descriptor))
        {
            try
            {
                if (WriteOwnedNode(path)) count++;
            }
            catch
            {
                // 单个文件失败不影响其余 CAD/语言目录处理。
            }
        }
        return count;
    }

    /// <summary>
    /// 删除指定 CAD 版本对应 <c>FixedProfile.aws</c> 中所有带 AFR 所有权标记的抑制节点。
    /// 用户手动设置（无标记）的同名节点不会被删除。
    /// </summary>
    /// <returns>实际清理的文件数量。</returns>
    public static int Cleanup(CadDescriptor descriptor)
    {
        if (IsAutoCadRunning()) return 0;

        int count = 0;
        foreach (var path in EnumerateTargetAwsFiles(descriptor))
        {
            try
            {
                if (RemoveOwnedNode(path)) count++;
            }
            catch
            {
                // 单个文件失败不影响整体卸载流程。
            }
        }
        return count;
    }

    /// <summary>判断 acad.exe 是否在运行；任何异常都视为"在运行"，从安全侧拒绝写入。</summary>
    private static bool IsAutoCadRunning()
    {
        try { return Process.GetProcessesByName("acad").Length > 0; }
        catch { return true; }
    }

    /// <summary>
    /// 枚举指定 CAD 版本对应的所有 <c>FixedProfile.aws</c> 候选路径。
    /// 形如 <c>%APPDATA%\Autodesk\AutoCAD &lt;year&gt;\R&lt;ver&gt;\&lt;lang&gt;\Support\Profiles\FixedProfile.aws</c>。
    /// </summary>
    private static IEnumerable<string> EnumerateTargetAwsFiles(CadDescriptor descriptor)
    {
        var versionTag = ExtractVersionTag(descriptor.RegistryBasePath); // R25.0
        if (string.IsNullOrEmpty(versionTag) || string.IsNullOrEmpty(descriptor.Version)) yield break;

        var appData    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // 与 AutoCAD 自写一致：以品牌 + 版本年份组成产品目录名（如 "AutoCAD 2025"）。
        var productDir = Path.Combine(appData, "Autodesk", $"{descriptor.Brand} {descriptor.Version}");
        if (!Directory.Exists(productDir)) yield break;

        var versionDir = Path.Combine(productDir, versionTag);
        if (!Directory.Exists(versionDir)) yield break;

        IEnumerable<string> langDirs;
        try { langDirs = Directory.EnumerateDirectories(versionDir, "*", SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var langDir in langDirs)
        {
            var awsPath = Path.Combine(langDir, "Support", "Profiles", FixedProfileFileName);
            if (File.Exists(awsPath)) yield return awsPath;
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
    /// 用户手动设置的同名节点（无 AFR 标记）一律保留不动。
    /// </summary>
    private static bool WriteOwnedNode(string path)
    {
        if (!File.Exists(path)) return false;

        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var profile = doc.Root;
        if (profile is null || profile.Name.LocalName != "Profile") return false;
        var ns = profile.GetDefaultNamespace();

        var storageRoot = profile.Element(ns + "StorageRoot")  ?? AddChild(profile,     ns + "StorageRoot");
        var acApData    = storageRoot.Element(ns + "AcApData") ?? AddChild(storageRoot, ns + "AcApData");
        var hideables   = acApData.Element(ns + "HideableDialogs") ?? AddChild(acApData, ns + "HideableDialogs");

        var existing = hideables.Elements(ns + "HideableDialog")
                                .FirstOrDefault(e => (string?)e.Attribute("id") == DialogId);

        // 用户预设节点（无 AFR 标记）— 保留不动。
        if (existing is not null && (string?)existing.Attribute(OwnershipAttr) != OwnershipToken)
            return false;

        // 自身节点已存在且与目标完全一致 — 跳过写入，避免每次安装都刷 mtime。
        if (existing is not null && IsOwnedNodeUpToDate(existing, ns))
            return false;

        existing?.Remove();
        hideables.Add(BuildOwnedNode(ns));
        SaveAtomically(doc, path);
        return true;
    }

    /// <summary>构造带 AFR 标记的目标节点（结构与 AutoCAD 自写一致）。</summary>
    private static XElement BuildOwnedNode(XNamespace ns)
    {
        XNamespace adWin = TaskDialogXmlns;
        var taskDialog = new XElement(adWin + "TaskDialog",
            new XAttribute("Source", TaskDialogSource),
            new XAttribute("Id",     DialogId));
        var preview = new XElement(ns + "Preview", taskDialog);
        return new XElement(ns + "HideableDialog",
            new XAttribute("id",          DialogId),
            new XAttribute("title",       DialogTitle),
            new XAttribute("category",    DialogTitle),
            new XAttribute("application", ""),
            new XAttribute("result",      DialogResult),
            new XAttribute(OwnershipAttr, OwnershipToken),
            preview);
    }

    /// <summary>判断已存在的自有节点结构是否已经与当前目标完全一致（用于跳过冗余写入）。</summary>
    private static bool IsOwnedNodeUpToDate(XElement existing, XNamespace ns)
    {
        if ((string?)existing.Attribute("title")       != DialogTitle)  return false;
        if ((string?)existing.Attribute("category")    != DialogTitle)  return false;
        if ((string?)existing.Attribute("application") != "")           return false;
        if ((string?)existing.Attribute("result")      != DialogResult) return false;

        var preview = existing.Element(ns + "Preview");
        if (preview is null) return false;

        XNamespace adWin = TaskDialogXmlns;
        var taskDialog = preview.Element(adWin + "TaskDialog");
        if (taskDialog is null) return false;
        if ((string?)taskDialog.Attribute("Source") != TaskDialogSource) return false;
        if ((string?)taskDialog.Attribute("Id")     != DialogId)         return false;

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

    /// <summary>
    /// 原子方式保存 XML：先写临时文件再 <see cref="File.Replace(string, string, string?, bool)"/>。
    /// 避免崩溃/断电时留下半截 .aws 导致 AutoCAD 拒绝加载该 Profile；不创建备份。
    /// </summary>
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

        try
        {
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            // 极少数文件系统不支持 Replace（部分网络共享）— 退化为非原子的 Copy + Delete。
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* 残留临时文件不影响主流程 */ }
        }
    }

    private static XElement AddChild(XElement parent, XName name)
    {
        var c = new XElement(name);
        parent.Add(c);
        return c;
    }
}
