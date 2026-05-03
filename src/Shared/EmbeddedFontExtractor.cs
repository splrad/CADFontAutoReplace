using System.IO;
using System.Reflection;

namespace AFR.Shared;

/// <summary>
/// 将程序集嵌入的 SHX 字体释放到目标目录的纯抽取工具。
/// <para>
/// 不依赖 AutoCAD、PlatformManager、Diagnostic 日志等环境，仅做"按资源名读流 → 写磁盘"。
/// 由两处共用：
/// <list type="bullet">
///   <item>插件侧 <c>AFR.Hosting.EmbeddedFontDeployer</c>：NETLOAD 加载或 CAD 启动时，
///         以 <c>Process.MainModule</c> 解析出 <c>&lt;acad.exe&gt;\Fonts</c> 调用本工具。</item>
///   <item>部署器侧 <c>AFR.Deployer.Services.EmbeddedFontPatcher</c>：用户通过 AFR 部署工具
///         安装插件时，以 HKLM 注册表 <c>AcadLocation</c> 拼出 <c>\Fonts</c> 调用本工具。</item>
/// </list>
/// 二者声明的内嵌资源逻辑名一致（<c>AFR.Fonts.&lt;file&gt;</c>），因此本工具不感知调用来源。
/// </para>
/// </summary>
internal static class EmbeddedFontExtractor
{
    /// <summary>内嵌的默认 SHX 主字体文件名。</summary>
    internal const string DefaultMainFont = "ming.shx";

    /// <summary>内嵌的默认 SHX 大字体文件名。</summary>
    internal const string DefaultBigFont = "tssdchn.shx";

    /// <summary>
    /// 内嵌的默认 TrueType 字体名（不参与文件释放，仅供注册表默认值使用）。
    /// 该字体由 Windows 自带，无需打包。
    /// </summary>
    internal const string DefaultTrueTypeFont = "宋体";

    /// <summary>内嵌资源名称统一前缀（与 .csproj/.projitems 中 LogicalName 约定一致）。</summary>
    internal const string ResourcePrefix = "AFR.Fonts.";

    /// <summary>需要释放的 SHX 字体文件名（与 <see cref="ResourcePrefix"/> 拼接得到资源名）。</summary>
    internal static readonly string[] EmbeddedFontFiles = [DefaultMainFont, DefaultBigFont];

    /// <summary>
    /// 将所有内嵌 SHX 字体释放到指定目录。已存在同名文件一律跳过，不覆盖用户已放置的字体，也不删除任何文件。
    /// </summary>
    /// <param name="assembly">承载嵌入资源的程序集（插件 DLL 或部署器 EXE）。</param>
    /// <param name="targetDirectory">目标 Fonts 目录的完整路径，必须已存在。</param>
    /// <param name="errorMessage">失败原因（可能仅描述其中一个文件）；全部成功或全部已存在时为 null。</param>
    /// <returns>true 表示所有目标字体均已就绪（释放成功或已存在）；false 表示至少一个释放失败。</returns>
    internal static bool ExtractAll(Assembly assembly, string targetDirectory, out string? errorMessage)
    {
        errorMessage = null;

        if (!Directory.Exists(targetDirectory))
        {
            errorMessage = $"目标目录不存在：{targetDirectory}";
            return false;
        }

        bool allSuccess = true;
        foreach (var fileName in EmbeddedFontFiles)
        {
            var targetPath = Path.Combine(targetDirectory, fileName);
            if (File.Exists(targetPath)) continue;

            if (!ExtractOne(assembly, ResourcePrefix + fileName, targetPath, out var err))
            {
                allSuccess = false;
                errorMessage ??= err;
            }
        }
        return allSuccess;
    }

    /// <summary>
    /// 从程序集嵌入资源提取单个文件到磁盘。已存在同名文件直接返回 true（视为成功）。
    /// </summary>
    internal static bool ExtractOne(Assembly assembly, string resourceName, string targetPath, out string? errorMessage)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                errorMessage = null;
                return true;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                errorMessage = $"未找到嵌入资源：{resourceName}";
                return false;
            }

            // CreateNew：与 File.Exists 形成 TOCTOU 兜底，绝不覆盖已存在文件。
            using var fileStream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.CopyTo(fileStream);
            errorMessage = null;
            return true;
        }
        catch (IOException ex) when (File.Exists(targetPath))
        {
            // CreateNew 的并发竞争：另一线程/进程刚刚写出同名文件——视为已就绪。
            _ = ex;
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"释放 {targetPath} 失败：{ex.Message}";
            return false;
        }
    }
}
