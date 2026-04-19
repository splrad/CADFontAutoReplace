using System.Diagnostics;
using System.IO;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Services;

/// <summary>
/// AutoCAD 运行环境配置，提供统一的字体搜索路径。
/// <para>
/// 作为唯一的路径来源，供 <see cref="AutoCadFontScanner"/> 和 LdFileHook 共用，
/// 避免两处分别硬编码目录导致扫描范围不一致。
/// </para>
/// </summary>
internal static class CadEnvironmentSettings
{
    /// <summary>
    /// 获取所有可能包含字体文件（SHX / TTF / TTC）的搜索目录。
    /// <para>
    /// 扫描范围（按优先级排列）：
    /// <list type="number">
    ///   <item>ACADPREFIX 系统变量指定的搜索路径（包含 Support 和 Fonts 等目录）。</item>
    ///   <item>AutoCAD 安装目录下的 Fonts 文件夹。</item>
    ///   <item>AppData 中用户配置的 Support 目录。</item>
    ///   <item>Windows 系统字体目录（C:\Windows\Fonts）。</item>
    /// </list>
    /// 返回值已去重，各目录仅出现一次。
    /// </para>
    /// </summary>
    public static List<string> GetAllFontSearchPaths()
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── 1. ACADPREFIX 搜索路径 ──
        try
        {
            var prefix = (string)AcadApp.GetSystemVariable("ACADPREFIX");
            if (!string.IsNullOrEmpty(prefix))
            {
                foreach (var dir in prefix.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    TryAdd(dir.Trim(), paths, seen);
            }
        }
        catch { }

        // ── 2. AutoCAD 安装目录 Fonts ──
        try
        {
            var processPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (processPath != null)
                TryAdd(Path.Combine(Path.GetDirectoryName(processPath)!, "Fonts"), paths, seen);
        }
        catch { }

        // ── 3. AppData 用户 Support 目录 ──
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string dir in Directory.GetDirectories(
                Path.Combine(appData, "Autodesk"), "AutoCAD *", SearchOption.TopDirectoryOnly))
            {
                foreach (string supportDir in Directory.GetDirectories(dir, "Support", SearchOption.AllDirectories))
                    TryAdd(supportDir, paths, seen);
            }
        }
        catch { }

        // ── 4. Windows 系统字体目录 ──
        try
        {
            TryAdd(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), paths, seen);
        }
        catch { }

        return paths;
    }

    private static void TryAdd(string path, List<string> paths, HashSet<string> seen)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && seen.Add(path))
            paths.Add(path);
    }
}
