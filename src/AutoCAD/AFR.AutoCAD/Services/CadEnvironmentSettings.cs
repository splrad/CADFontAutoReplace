using System.Diagnostics;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Services;

/// <summary>
/// AutoCAD 字体搜索路径来源。
/// <para>
/// 供共享字体索引使用，避免扫描逻辑散落在检测、UI 和 Hook 中。
/// </para>
/// </summary>
internal static class CadEnvironmentSettings
{
    /// <summary>
    /// 获取 CAD 字体搜索目录，按 AutoCAD 搜索路径优先。
    /// <para>
    /// 扫描范围（按优先级排列）：
    /// <list type="number">
    ///   <item>ACADPREFIX 系统变量指定的搜索路径（包含 Support 和 Fonts 等目录）。</item>
    ///   <item>HostApplicationServices 暴露的 Roamable/Local/AllUsers 根目录下有限的 Fonts/Support 子目录。</item>
    ///   <item>AutoCAD 进程目录下的 Fonts 文件夹。</item>
    /// </list>
    /// 不递归扫描 AppData 配置树，避免启动和 Hook 初始化阶段放大 I/O。
    /// </para>
    /// </summary>
    public static List<string> GetAllFontSearchPaths()
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddAcadPrefixPaths(paths, seen);
        AddHostApplicationPaths(paths, seen);
        AddProcessFontsPath(paths, seen);

        if (paths.Count == 0)
        {
            DiagnosticLogger.Skip(
                "CadEnvironmentSettings",
                "GetAllFontSearchPaths",
                "CAD 字体搜索路径为空");
        }
        else
        {
            DiagnosticLogger.Ok(
                "CadEnvironmentSettings",
                "GetAllFontSearchPaths",
                "CAD 字体搜索路径已收集",
                new Dictionary<string, object?>
                {
                    ["pathCount"] = paths.Count,
                    ["paths"] = string.Join(" | ", paths)
                });
        }

        return paths;
    }

    private static void AddAcadPrefixPaths(List<string> paths, HashSet<string> seen)
    {
        try
        {
            var prefix = (string)AcadApp.GetSystemVariable("ACADPREFIX");
            if (!string.IsNullOrEmpty(prefix))
            {
                foreach (var dir in prefix.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    TryAdd(dir.Trim(), paths, seen);
            }
        }
        catch { }
    }

    private static void AddHostApplicationPaths(List<string> paths, HashSet<string> seen)
    {
        try
        {
            HostApplicationServices? host = HostApplicationServices.Current;
            if (host == null)
                return;

            AddKnownCadFontSubdirectories(host.RoamableRootFolder, paths, seen);
            AddKnownCadFontSubdirectories(host.LocalRootFolder, paths, seen);
            AddKnownCadFontSubdirectories(host.AllUsersRootFolder, paths, seen);
        }
        catch { }
    }

    private static void AddProcessFontsPath(List<string> paths, HashSet<string> seen)
    {
        try
        {
            var processPath = Process.GetCurrentProcess().MainModule?.FileName;
            var processDirectory = string.IsNullOrEmpty(processPath)
                ? null
                : Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(processDirectory))
                TryAdd(Path.Combine(processDirectory, "Fonts"), paths, seen);
        }
        catch { }
    }

    private static void AddKnownCadFontSubdirectories(string? root, List<string> paths, HashSet<string> seen)
    {
        string normalizedRoot = root?.Trim() ?? string.Empty;
        if (normalizedRoot.Length == 0)
            return;

        TryAdd(normalizedRoot, paths, seen);
        TryAdd(Path.Combine(normalizedRoot, "Fonts"), paths, seen);
        TryAdd(Path.Combine(normalizedRoot, "Support"), paths, seen);
    }

    private static void TryAdd(string path, List<string> paths, HashSet<string> seen)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && seen.Add(path))
            paths.Add(path);
    }
}
