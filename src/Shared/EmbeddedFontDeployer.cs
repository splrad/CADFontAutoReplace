using System.Diagnostics;
using System.IO;
using AFR.Services;
using AFR.Shared;

namespace AFR.Hosting;

/// <summary>
/// 插件 NETLOAD 路径下，把内嵌的默认 SHX 字体释放到当前 CAD 进程的 Fonts 目录。
/// <para>
/// 与具体 CAD 品牌无关：通过 <see cref="Process.MainModule"/> 定位宿主可执行文件
/// （acad.exe / bricscad.exe / 其他品牌主程序），在其同级 <c>Fonts</c> 子目录下落盘。
/// 实际 IO 委托给共享的 <see cref="EmbeddedFontExtractor"/>，与部署工具 EXE 共用同一套
/// 资源命名与跳过策略；本类只负责"在宿主进程上下文里"定位 Fonts 目录。
/// 已存在同名文件一律跳过，不覆盖、不删除任何用户文件。
/// </para>
/// </summary>
internal static class EmbeddedFontDeployer
{
    /// <summary>内嵌的默认 SHX 主字体文件名。</summary>
    internal const string DefaultMainFont = EmbeddedFontExtractor.DefaultMainFont;

    /// <summary>内嵌的默认 SHX 大字体文件名。</summary>
    internal const string DefaultBigFont = EmbeddedFontExtractor.DefaultBigFont;

    /// <summary>内嵌的默认 TrueType 替换字体名。</summary>
    internal const string DefaultTrueTypeFont = EmbeddedFontExtractor.DefaultTrueTypeFont;

    /// <summary>
    /// 将所有内嵌字体释放到当前宿主进程同级的 Fonts 目录。
    /// </summary>
    /// <returns>true 表示所有字体均已就绪；false 表示无法定位 Fonts 目录或至少一个释放失败。</returns>
    internal static bool Deploy()
    {
        var fontsDir = GetCadFontsDirectory();
        if (fontsDir is null)
        {
            DiagnosticLogger.Log("字体部署", "无法定位 CAD Fonts 目录");
            return false;
        }

        var assembly = typeof(EmbeddedFontDeployer).Assembly;
        var ok = EmbeddedFontExtractor.ExtractAll(assembly, fontsDir, out var error);
        if (!ok && error is not null)
            DiagnosticLogger.Log("字体部署", error);
        return ok;
    }

    private static string? GetCadFontsDirectory()
    {
        try
        {
            var processPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (processPath is null) return null;

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
}
