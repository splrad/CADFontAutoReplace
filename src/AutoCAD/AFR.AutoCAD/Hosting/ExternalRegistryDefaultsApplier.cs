using Microsoft.Win32;
using System.Reflection;
using System.Text.RegularExpressions;
using AFR.Platform;
using AFR.Services;

namespace AFR.Hosting;

/// <summary>
/// NETLOAD 直装路径下应用 / 清理 <see cref="RegistryDefaultDwordAtAttribute"/> 声明的外部注册表默认值。
/// <para>
/// 部署工具与本类共用同一套语义（声明源同一份），因此 NETLOAD 与部署工具二选一安装均能拿到一致结果：
/// <list type="bullet">
///   <item><description>外部键写到 <c>&lt;ProfileSubKey&gt;\&lt;SubPath&gt;</c>（如 <c>FixedProfile\General Configuration</c>）。</description></item>
///   <item><description>所有权标记写到 <c>Applications\&lt;AppName&gt;\__Owned\&lt;SubPath&gt;</c>，仅在卸载时驱动清理，
///         保留用户安装前的预设以及安装后中途的手动修改。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class ExternalRegistryDefaultsApplier
{
    private const string OwnedSubKey = "__Owned";

    /// <summary>
    /// 对所有匹配当前 CAD 版本的配置文件写入声明的外部默认值，并在需要时打所有权标记。
    /// 写入策略与部署工具一致：缺失 → 写入并打标；现值与期望相同 → 保留视为用户预设，不打标；
    /// 现值不同 → 仅 <c>ForceOverwrite=true</c> 时覆写并打标。
    /// </summary>
    public static void Apply()
    {
        var attrs = typeof(ExternalRegistryDefaultsApplier).Assembly
            .GetCustomAttributes<RegistryDefaultDwordAtAttribute>()
            .ToArray();
        if (attrs.Length == 0) return;

        foreach (var profile in EnumerateProfiles())
        {
            foreach (var a in attrs)
            {
                ApplyOne(profile, a);
            }
        }
    }

    /// <summary>
    /// 卸载时按所有权标记清理外部键值。仅当外部键现值仍等于打标时记录的值才删除。
    /// </summary>
    public static void Cleanup()
    {
        foreach (var profile in EnumerateProfiles())
        {
            CleanupProfile(profile);
        }
    }

    private static void ApplyOne(string profile, RegistryDefaultDwordAtAttribute a)
    {
        var basePath = PlatformManager.Platform.RegistryBasePath;
        var appName  = PlatformManager.Platform.AppName;

        var targetPath = $@"{basePath}\{profile}\{a.SubPath}";
        var appPath    = $@"{basePath}\{profile}\Applications\{appName}";

        try
        {
            using var target = Registry.CurrentUser.CreateSubKey(targetPath, true);
            if (target is null) return;

            var existing = target.GetValue(a.Name);
            bool wrote;
            if (existing is null)
            {
                target.SetValue(a.Name, a.Value, RegistryValueKind.DWord);
                wrote = true;
            }
            else if (existing is int cur && cur == a.Value)
            {
                wrote = false;
            }
            else if (a.ForceOverwrite)
            {
                target.SetValue(a.Name, a.Value, RegistryValueKind.DWord);
                wrote = true;
            }
            else
            {
                wrote = false;
            }

            if (wrote && a.RemoveOnUninstall)
            {
                using var ownedRoot = Registry.CurrentUser.CreateSubKey(
                    $@"{appPath}\{OwnedSubKey}\{a.SubPath}", true);
                ownedRoot?.SetValue(a.Name, a.Value, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("ExternalDefaults", $"写入 {targetPath}\\{a.Name} 失败：{ex.Message}");
        }
    }

    private static void CleanupProfile(string profile)
    {
        var basePath = PlatformManager.Platform.RegistryBasePath;
        var appName  = PlatformManager.Platform.AppName;
        var appPath  = $@"{basePath}\{profile}\Applications\{appName}";
        var profileRoot = $@"{basePath}\{profile}";

        try
        {
            // 先把所有需要清理的相对子路径快照出来，避免迭代过程中删除导致键句柄状态不稳定。
            List<string> ownedSubPaths;
            using (var ownedRoot = Registry.CurrentUser.OpenSubKey($@"{appPath}\{OwnedSubKey}", false))
            {
                if (ownedRoot is null) return;
                ownedSubPaths = EnumerateOwnedSubPaths(ownedRoot, currentPrefix: "").ToList();
            }

            using (var ownedRoot = Registry.CurrentUser.OpenSubKey($@"{appPath}\{OwnedSubKey}", false))
            {
                if (ownedRoot is null) return;

                foreach (var rel in ownedSubPaths)
                {
                    using var ownedNode = ownedRoot.OpenSubKey(rel, false);
                    if (ownedNode is null) continue;

                    using var target = Registry.CurrentUser.OpenSubKey($@"{profileRoot}\{rel}", true);
                    if (target is null) continue;

                    foreach (var name in ownedNode.GetValueNames())
                    {
                        var owned = ownedNode.GetValue(name);
                        var actual = target.GetValue(name);
                        if (owned is int oi && actual is int ai && oi == ai)
                        {
                            try { target.DeleteValue(name, throwOnMissingValue: false); } catch { }
                        }
                    }
                }
            }

            // 清理完外部值后，把整个 __Owned 子树一并删除。否则下次 Apply→Cleanup 周期里
            // 残留标记会把"用户安装前的预设"误判为"我们的所有物"导致误删。
            try
            {
                using var appKey = Registry.CurrentUser.OpenSubKey(appPath, true);
                appKey?.DeleteSubKeyTree(OwnedSubKey, throwOnMissingSubKey: false);
            }
            catch { /* 子树不存在或权限不足都不影响主流程 */ }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("ExternalDefaults", $"清理标记失败：{ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateOwnedSubPaths(RegistryKey root, string currentPrefix)
    {
        if (currentPrefix.Length > 0)
        {
            using var here = root.OpenSubKey(currentPrefix, false);
            if (here is not null && here.ValueCount > 0)
                yield return currentPrefix;
        }

        // currentPrefix 为空时直接复用 root 句柄，不要 using —— 否则迭代器 Dispose 时会把调用方的 root 也释放掉。
        RegistryKey? node = currentPrefix.Length == 0 ? root : root.OpenSubKey(currentPrefix, false);
        if (node is null) yield break;
        try
        {
            foreach (var child in node.GetSubKeyNames())
            {
                var next = currentPrefix.Length == 0 ? child : currentPrefix + "\\" + child;
                foreach (var p in EnumerateOwnedSubPaths(root, next))
                    yield return p;
            }
        }
        finally
        {
            if (!ReferenceEquals(node, root)) node.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateProfiles()
    {
        var basePath = PlatformManager.Platform.RegistryBasePath;
        var pattern  = new Regex(PlatformManager.Platform.RegistryKeyPattern, RegexOptions.Compiled);
        foreach (var name in RegistryService.GetSubKeyNames(Registry.CurrentUser, basePath))
        {
            if (pattern.IsMatch(name)) yield return name;
        }
    }
}
