using AFR.Deployer.Models;
using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;

namespace AFR.Deployer.Services;

/// <summary>
/// 插件卸载服务：删除物理 DLL 文件（若存在）并清理注册表自动加载条目。
/// </summary>
internal static class PluginUninstaller
{
    /// <summary>
    /// 卸载指定 CAD 配置文件实例中的插件。
    /// <para>
    /// 流程：
    /// <list type="number">
    ///   <item>重新读取注册表 LOADER 值，获取 DLL 实际路径（防手动修改）。</item>
    ///   <item>若 DLL 文件存在则尝试删除；若已被手动删除则跳过文件操作。</item>
    ///   <item>删除注册表中该配置文件实例下的 AppName 子键。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="installation">目标 CAD 配置文件实例（来自最新一次扫描结果）。</param>
    /// <param name="warningMessage">部分成功时的警告（如删除文件失败但注册表已清理），完全成功时为 null。</param>
    /// <returns>true 表示注册表项已成功清理（文件操作失败不影响返回值）。</returns>
    internal static bool TryUninstall(CadInstallation installation, out string? warningMessage)
    {
        warningMessage = null;

        // 1. 重新从注册表读取 LOADER，防止 UI 缓存与实际不符
        string? dllPath;
        try
        {
            using var appKey = Registry.CurrentUser.OpenSubKey(installation.RegistryAppPath, false);
            dllPath = appKey?.GetValue("LOADER") as string;

            // 1b. 按 __Owned 标记清理外部键（FixedProfile 等），仅在标记值与现值一致时清除。
            CleanupOwnedExternalValues(installation, appKey);
        }
        catch (Exception ex)
        {
            warningMessage = $"读取注册表失败：{ex.Message}";
            return false;
        }

        // 2. 删除 DLL 文件（若存在）
        if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
        {
            try
            {
                File.Delete(dllPath);
            }
            catch (Exception ex)
            {
                // 文件删除失败（如权限不足、文件被占用）：记录警告但继续清理注册表
                warningMessage = $"DLL 文件删除失败（{ex.Message}），注册表条目仍将被清理。";
            }
        }

        // 3. 删除注册表 AppName 子键
        // RegistryAppPath 末尾为 "...\Applications\{AppName}"，直接用已知的 AppName 定位
        try
        {
            var applicationsPath = installation.RegistryAppPath[
                ..^(installation.Descriptor.AppName.Length + 1)]; // 去掉 "\AppName"
            using var parentKey = Registry.CurrentUser.OpenSubKey(applicationsPath, true);
            parentKey?.DeleteSubKeyTree(installation.Descriptor.AppName, throwOnMissingSubKey: false);

            return true;
        }
        catch (Exception ex)
        {
            warningMessage = (warningMessage is null ? "" : warningMessage + " | ")
                           + $"删除注册表项失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 按 <c>Applications\&lt;AppName&gt;\__Owned\&lt;SubPath&gt;</c> 下的所有权标记，
    /// 反向清理 <c>ProfileSubKey\&lt;SubPath&gt;</c> 中由我们写入的外部键值。
    /// <para>
    /// 仅当外部键的现值与所有权标记中记录的值完全一致时才删除，从而保留用户在安装后中途的手动修改。
    /// </para>
    /// </summary>
    private static void CleanupOwnedExternalValues(CadInstallation installation, RegistryKey? appKey)
    {
        if (appKey is null) return;

        try
        {
            using var ownedRoot = appKey.OpenSubKey("__Owned", false);
            if (ownedRoot is null) return;

            var profileRoot = $@"{installation.Descriptor.RegistryBasePath}\{installation.ProfileSubKey}";

            // 递归遍历 __Owned 下的每个子路径，子路径与外部目标路径一一对应。
            foreach (var relSubPath in EnumerateOwnedSubPaths(ownedRoot, currentPrefix: ""))
            {
                using var ownedNode = ownedRoot.OpenSubKey(relSubPath, false);
                if (ownedNode is null) continue;

                var targetPath = $@"{profileRoot}\{relSubPath}";
                using var target = Registry.CurrentUser.OpenSubKey(targetPath, true);
                if (target is null) continue;

                foreach (var name in ownedNode.GetValueNames())
                {
                    var owned = ownedNode.GetValue(name);
                    var actual = target.GetValue(name);
                    if (owned is int oi && actual is int ai && oi == ai)
                    {
                        try { target.DeleteValue(name, throwOnMissingValue: false); }
                        catch { /* 单值删除失败不影响其他清理 */ }
                    }
                }
            }
        }
        catch
        {
            // 清理标记失败不应阻断整体卸载流程。
        }
    }

    /// <summary>
    /// 枚举 <c>__Owned</c> 下所有"叶子或带值"的相对子路径（含中间含值的节点）。
    /// </summary>
    private static IEnumerable<string> EnumerateOwnedSubPaths(RegistryKey root, string currentPrefix)
    {
        // 仅当当前节点本身有值时才需要返回它（叶子或中间节点都可能存值）。
        if (currentPrefix.Length > 0)
        {
            using var here = root.OpenSubKey(currentPrefix, false);
            if (here is not null && here.ValueCount > 0)
                yield return currentPrefix;
        }

        // currentPrefix 为空时直接复用 root，避免迭代器 Dispose 时把调用方的根句柄一并释放。
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
}
