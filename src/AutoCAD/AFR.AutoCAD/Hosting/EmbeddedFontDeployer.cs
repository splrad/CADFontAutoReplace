using System.Diagnostics;
using System.IO;
using System.Reflection;
using AFR.Services;

namespace AFR.Hosting;

/// <summary>
/// 将插件内嵌的默认 SHX 字体文件释放到 AutoCAD 的 Fonts 目录。
/// <para>
/// 仅在首次安装（注册表键不存在）时由 <see cref="AppInitializer"/> 调用。
/// 若目标目录已存在同名文件则跳过，不覆盖用户自行放置的字体。
/// 不影响用户已安装的字体文件，且不会删除任何文件。仅确保内嵌的默认字体在目标环境中可用，确保插件开箱即用。
/// </para>
/// </summary>
internal static class EmbeddedFontDeployer
{
    /// <summary>内嵌的默认 SHX 主字体文件名。</summary>
    internal const string DefaultMainFont = "K_roms.shx";

    /// <summary>内嵌的默认 SHX 大字体文件名。</summary>
    internal const string DefaultBigFont = "tssdchn.shx";

    /// <summary>内嵌的默认 TrueType 替换字体文件名。</summary>
    internal const string DefaultTrueTypeFont = "simsun.ttc";

    // 嵌入资源名称前缀
    private const string ResourcePrefix = "AFR.Fonts.";

    // 需要释放的字体文件列表
    private static readonly string[] EmbeddedFontFiles = [DefaultMainFont, DefaultBigFont];

    /// <summary>
    /// 将内嵌字体文件释放到 AutoCAD Fonts 目录。
    /// <para>
    /// 通过当前进程路径（acad.exe）定位 AutoCAD 安装目录，
    /// 在其下的 Fonts 子目录中释放字体文件。已存在同名文件则跳过。
    /// </para>
    /// </summary>
    /// <returns>true 表示所有字体均已就绪（释放成功或已存在）；false 表示至少一个字体释放失败。</returns>
    internal static bool Deploy()
    {
        var fontsDir = GetCadFontsDirectory();
        if (fontsDir == null)
        {
            DiagnosticLogger.Log("字体部署", "无法定位 AutoCAD Fonts 目录");
            return false;
        }

        var assembly = typeof(EmbeddedFontDeployer).Assembly;
        bool allSuccess = true;

        foreach (var fileName in EmbeddedFontFiles)
        {
            var targetPath = Path.Combine(fontsDir, fileName);

            // 目标目录已存在同名文件则跳过，不覆盖
            if (File.Exists(targetPath))
            {
                DiagnosticLogger.Log("字体部署", $"已存在，跳过: {targetPath}");
                continue;
            }

            if (!ExtractResource(assembly, ResourcePrefix + fileName, targetPath))
            {
                allSuccess = false;
            }
        }

        return allSuccess;
    }

    /// <summary>
    /// 获取 AutoCAD 安装目录下的 Fonts 子目录路径。
    /// </summary>
    private static string? GetCadFontsDirectory()
    {
        try
        {
            var processPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (processPath == null) return null;

            var fontsDir = Path.Combine(Path.GetDirectoryName(processPath)!, "Fonts");
            if (!Directory.Exists(fontsDir))
            {
                DiagnosticLogger.Log("字体部署", $"Fonts 目录不存在: {fontsDir}");
                return null;
            }
            return fontsDir;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("字体部署", $"获取 Fonts 目录失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从程序集嵌入资源中提取文件到磁盘。
    /// </summary>
    /// <param name="assembly">包含嵌入资源的程序集。</param>
    /// <param name="resourceName">嵌入资源的逻辑名称。</param>
    /// <param name="targetPath">目标文件的完整路径。</param>
    /// <returns>true 表示提取成功；false 表示失败。</returns>
    private static bool ExtractResource(Assembly assembly, string resourceName, string targetPath)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                DiagnosticLogger.Log("字体部署", $"未找到嵌入资源: {resourceName}");
                return false;
            }

            using var fileStream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write);
            stream.CopyTo(fileStream);

            DiagnosticLogger.Log("字体部署", $"已释放: {targetPath}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("字体部署", $"释放失败 {targetPath}: {ex.Message}");
            return false;
        }
    }
}