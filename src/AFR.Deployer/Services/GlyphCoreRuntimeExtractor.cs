using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace AFR.Deployer.Services;

/// <summary>
/// 在部署器安装插件 DLL 后，提前将 ONNX 原生运行库从 DLL 嵌入资源解压到插件目录。
/// <para>
/// 插件在 AutoCAD 内首次使用文枢 AI 评分时，会在运行时解压原生库。
/// 部署器提前完成这一步，避免 CAD 首次加载时写磁盘，并消除多版本 DLL 的加载顺序竞争问题。
/// </para>
/// <para>
/// 提取目录与插件运行时一致：<c>&lt;dllDirectory&gt;\GlyphCoreRuntime\TextRepair\OnnxRuntime\&lt;cacheKey&gt;\</c>。
/// cacheKey 由程序集版本号与原生库内容哈希组成，与 <c>GlyphCoreTextRepairEmbeddedOnnxScorer</c> 中的算法完全相同。
/// </para>
/// <para>
/// 失败时只产生警告，不中断安装流程：运行时仍有 <c>%LOCALAPPDATA%</c> 回退路径。
/// </para>
/// </summary>
internal static class GlyphCoreRuntimeExtractor
{
    private const string NativeRuntimeResourceName   = "onnxruntime.dll";
    private const string ProvidersSharedResourceName = "onnxruntime_providers_shared.dll";
    private const string CacheSubPath = "OnnxRuntime";

    /// <summary>
    /// 从已释放的插件 DLL 中提取 ONNX 原生运行库到插件目录缓存。
    /// </summary>
    /// <param name="dllPath">已释放到磁盘的插件 DLL 完整路径。</param>
    /// <param name="warningMessage">提取失败时的警告信息，成功时为 null。</param>
    /// <returns>true 表示提取成功或资源不存在（无模型 DLL 时静默跳过）；false 表示提取失败并输出警告。</returns>
    internal static bool TryExtract(string dllPath, out string? warningMessage)
    {
        warningMessage = null;

        try
        {
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream);

            // 读取程序集版本（用于 cacheKey 的第一段）
            var mdReader = pe.GetMetadataReader();
            string assemblyVersion = ReadAssemblyVersion(mdReader);

            // 读取两个原生库资源字节
            byte[]? nativeRuntime = ReadEmbeddedResource(pe, mdReader, NativeRuntimeResourceName);
            if (nativeRuntime == null || nativeRuntime.Length == 0)
                return true; // 无模型的 DLL（如开发环境无嵌入模型），静默跳过

            byte[]? providersShared = ReadEmbeddedResource(pe, mdReader, ProvidersSharedResourceName);
            if (providersShared == null || providersShared.Length == 0)
                return true;

            // 计算 cacheKey（与插件运行时算法完全一致）
            string cacheKey = BuildCacheKey(assemblyVersion, nativeRuntime, providersShared);

            string dllDirectory = Path.GetDirectoryName(dllPath)!;
            string cacheDir = Path.Combine(dllDirectory, CacheSubPath, cacheKey);

            Directory.CreateDirectory(cacheDir);
            WriteIfChanged(Path.Combine(cacheDir, NativeRuntimeResourceName), nativeRuntime);
            WriteIfChanged(Path.Combine(cacheDir, ProvidersSharedResourceName), providersShared);

            return true;
        }
        catch (Exception ex)
        {
            warningMessage = $"ONNX 原生运行库预释放失败（{Path.GetFileName(dllPath)}）：{ex.Message}。CAD 加载时将自动回退到 %LOCALAPPDATA% 缓存路径。";
            return false;
        }
    }

    // ── 嵌入资源读取 ────────────────────────────────────────────────────

    /// <summary>
    /// 使用 <see cref="PEReader"/> 从 .NET 托管 DLL 中读取指定名称的嵌入资源字节。
    /// 不加载程序集，不触发依赖解析，可安全处理任意目标 CAD 版本的插件 DLL。
    /// </summary>
    private static byte[]? ReadEmbeddedResource(PEReader pe, MetadataReader mdReader, string resourceName)
    {
        var corHeader = pe.PEHeaders.CorHeader;
        if (corHeader == null)
            return null;

        int resourcesRva = corHeader.ResourcesDirectory.RelativeVirtualAddress;

        foreach (var handle in mdReader.ManifestResources)
        {
            var resource = mdReader.GetManifestResource(handle);
            if (!resource.Implementation.IsNil)
                continue; // 跳过外部资源

            if (mdReader.GetString(resource.Name) != resourceName)
                continue;

            // 资源数据布局：[4字节长度][数据字节]
            var sectionData = pe.GetSectionData(resourcesRva + (int)resource.Offset);
            var reader = sectionData.GetReader();
            int length = reader.ReadInt32();
            if (length <= 0)
                return null;

            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = reader.ReadByte();
            return bytes;
        }

        return null;
    }

    private static string ReadAssemblyVersion(MetadataReader mdReader)
    {
        if (!mdReader.IsAssembly)
            return "unknown";

        var version = mdReader.GetAssemblyDefinition().Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    // ── cacheKey 算法（与插件运行时保持一致）────────────────────────────

    private static string BuildCacheKey(string version, byte[] nativeRuntime, byte[] providersShared)
    {
        string key = version + "-" + ShortHash(nativeRuntime) + ShortHash(providersShared);

        foreach (char invalid in Path.GetInvalidFileNameChars())
            key = key.Replace(invalid, '_');

        return key;
    }

    private static string ShortHash(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return BitConverter.ToString(hash, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
    }

    // ── 写入辅助 ─────────────────────────────────────────────────────────

    private static void WriteIfChanged(string path, byte[] data)
    {
        if (File.Exists(path))
        {
            try
            {
                byte[] existing = File.ReadAllBytes(path);
                if (existing.SequenceEqual(data))
                    return;
            }
            catch
            {
                // 读取失败时重新写入
            }
        }

        File.WriteAllBytes(path, data);
    }
}
