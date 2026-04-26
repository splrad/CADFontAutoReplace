using System.IO;
using System.Reflection;

namespace AFR.Deployer.Services;

/// <summary>
/// 从 EXE 程序集的嵌入资源中提取插件 DLL 文件到目标目录。
/// </summary>
internal static class EmbeddedResourceExtractor
{
    /// <summary>
    /// 从嵌入资源提取指定 DLL 到目标目录。
    /// <para>
    /// 若目标文件已存在则覆盖（更新场景）。
    /// 若嵌入资源不存在（开发环境 Resources\ 目录中缺少该 DLL），返回 false 并输出原因。
    /// </para>
    /// </summary>
    /// <param name="resourceKey">嵌入资源的清单名称（如 "AFR.Deployer.Resources.AFR-ACAD2025.dll"）。</param>
    /// <param name="targetDirectory">目标目录路径，不存在时会自动创建。</param>
    /// <param name="fileName">释放后的文件名（如 "AFR-ACAD2025.dll"）。</param>
    /// <param name="errorMessage">失败时的原因描述，成功时为 null。</param>
    /// <returns>true 表示提取成功；false 表示失败。</returns>
    internal static bool TryExtract(
        string resourceKey,
        string targetDirectory,
        string fileName,
        out string? errorMessage)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceKey);

        if (stream is null)
        {
            errorMessage = $"嵌入资源未找到：{resourceKey}。请确认该版本的插件 DLL 已放入 Resources\\ 目录并重新编译。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
            var targetPath = Path.Combine(targetDirectory, fileName);

            using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fs);

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"写入文件失败：{ex.Message}";
            return false;
        }
    }
}
