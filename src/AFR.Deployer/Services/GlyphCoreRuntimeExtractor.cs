using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace AFR.Deployer.Services;

/// <summary>
/// 在部署器安装插件 DLL 后，提前将 ONNX 原生运行库从 DLL 嵌入资源释放到插件目录。
/// <para>
/// 释放目录与插件运行时一致：<c>&lt;dllDirectory&gt;\OnnxRuntime\&lt;abiKey&gt;\</c>。
/// <c>abiKey</c> 来自插件内嵌的 ONNX Runtime manifest，例如 <c>ort-1.18.0-win-x64</c>。
/// </para>
/// <para>
/// 同 ABI 且清单/文件完整时跳过写入；同 ABI 但内容不一致时覆盖该 ABI 目录；
/// 不会覆盖或删除其他 ABI 目录。失败时只产生警告，不中断安装流程。
/// </para>
/// </summary>
internal static class GlyphCoreRuntimeExtractor
{
    private const string NativeRuntimeResourceName = "onnxruntime.dll";
    private const string ProvidersSharedResourceName = "onnxruntime_providers_shared.dll";
    private const string RuntimeManifestResourceName = "AFR.GlyphCore.OnnxRuntimeManifest.json";
    private const string RuntimeManifestFileName = "AFR.OnnxRuntime.manifest.json";
    private const string RuntimeRootDirectoryName = "OnnxRuntime";

    /// <summary>
    /// 从已释放的插件 DLL 中提取 ONNX 原生运行库到插件目录 ABI 缓存。
    /// </summary>
    /// <param name="dllPath">已释放到磁盘的插件 DLL 完整路径。</param>
    /// <param name="warningMessage">提取失败时的警告信息，成功时为 null。</param>
    /// <returns>true 表示提取成功、已就绪或目标 DLL 不含 GlyphCore runtime manifest；false 表示提取失败并输出警告。</returns>
    internal static bool TryExtract(string dllPath, out string? warningMessage)
    {
        warningMessage = null;

        try
        {
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream);
            MetadataReader mdReader = pe.GetMetadataReader();

            byte[]? manifestBytes = ReadEmbeddedResource(pe, mdReader, RuntimeManifestResourceName);
            if (manifestBytes == null || manifestBytes.Length == 0)
                return true;

            string embeddedManifestText = Encoding.UTF8.GetString(manifestBytes);
            RuntimeManifest? manifest = RuntimeManifest.Parse(embeddedManifestText, out string manifestError);
            if (manifest == null)
            {
                warningMessage = $"ONNX Runtime 清单无效（{Path.GetFileName(dllPath)}）：{manifestError}";
                return false;
            }

            string dllDirectory = Path.GetDirectoryName(dllPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dllDirectory))
            {
                warningMessage = $"ONNX Runtime 预释放失败（{Path.GetFileName(dllPath)}）：无法解析插件 DLL 目录。";
                return false;
            }

            string runtimeDirectory = Path.Combine(dllDirectory, RuntimeRootDirectoryName, manifest.AbiKey);
            if (IsRuntimeDirectoryCurrent(runtimeDirectory, manifest))
                return true;

            byte[]? nativeRuntime = ReadEmbeddedResource(pe, mdReader, NativeRuntimeResourceName);
            if (nativeRuntime == null || nativeRuntime.Length == 0)
            {
                warningMessage = $"ONNX Runtime 预释放失败（{Path.GetFileName(dllPath)}）：缺少 {NativeRuntimeResourceName} 嵌入资源。";
                return false;
            }

            byte[]? providersShared = ReadEmbeddedResource(pe, mdReader, ProvidersSharedResourceName);
            if (providersShared == null || providersShared.Length == 0)
            {
                warningMessage = $"ONNX Runtime 预释放失败（{Path.GetFileName(dllPath)}）：缺少 {ProvidersSharedResourceName} 嵌入资源。";
                return false;
            }

            Directory.CreateDirectory(runtimeDirectory);
            WriteIfMismatch(Path.Combine(runtimeDirectory, manifest.NativeRuntime.Name), nativeRuntime, manifest.NativeRuntime);
            WriteIfMismatch(Path.Combine(runtimeDirectory, manifest.ProvidersShared.Name), providersShared, manifest.ProvidersShared);
            WriteTextIfChanged(Path.Combine(runtimeDirectory, RuntimeManifestFileName), embeddedManifestText);

            return true;
        }
        catch (Exception ex)
        {
            warningMessage = $"ONNX Runtime 预释放失败（{Path.GetFileName(dllPath)}）：{ex.Message}。CAD 启动时仍会再次尝试检查并补齐。";
            return false;
        }
    }

    private static bool IsRuntimeDirectoryCurrent(string runtimeDirectory, RuntimeManifest expected)
    {
        string manifestPath = Path.Combine(runtimeDirectory, RuntimeManifestFileName);
        if (!File.Exists(manifestPath))
            return false;

        string diskManifestText;
        try
        {
            diskManifestText = File.ReadAllText(manifestPath, Encoding.UTF8);
        }
        catch
        {
            return false;
        }

        RuntimeManifest? diskManifest = RuntimeManifest.Parse(diskManifestText, out _);
        if (diskManifest == null || !expected.Matches(diskManifest))
            return false;

        return FileMatchesManifest(runtimeDirectory, expected.NativeRuntime)
               && FileMatchesManifest(runtimeDirectory, expected.ProvidersShared);
    }

    private static bool FileMatchesManifest(string runtimeDirectory, RuntimeFileManifest file)
    {
        string path = Path.Combine(runtimeDirectory, file.Name);
        if (!File.Exists(path))
            return false;

        try
        {
            var info = new FileInfo(path);
            if (info.Length != file.Length || info.Length <= 0)
                return false;

            return string.Equals(Sha256Hex(path), file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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
                continue;

            if (mdReader.GetString(resource.Name) != resourceName)
                continue;

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

    private static void WriteIfMismatch(string path, byte[] data, RuntimeFileManifest expected)
    {
        if (FileMatchesManifest(Path.GetDirectoryName(path) ?? string.Empty, expected))
            return;

        WriteBytesAtomically(path, data);
    }

    private static void WriteTextIfChanged(string path, string text)
    {
        if (File.Exists(path))
        {
            try
            {
                if (string.Equals(File.ReadAllText(path, Encoding.UTF8), text, StringComparison.Ordinal))
                    return;
            }
            catch
            {
                // Re-write below.
            }
        }

        WriteBytesAtomically(path, new UTF8Encoding(false).GetBytes(text));
    }

    private static void WriteBytesAtomically(string path, byte[] data)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tempPath, data);

        if (File.Exists(path))
            File.Replace(tempPath, path, null);
        else
            File.Move(tempPath, path);
    }

    private static string Sha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(stream);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            builder.Append(b.ToString("x2"));
        return builder.ToString();
    }

    private sealed class RuntimeManifest
    {
        private RuntimeManifest(
            string abiKey,
            string packageVersion,
            string platform,
            RuntimeFileManifest nativeRuntime,
            RuntimeFileManifest providersShared)
        {
            AbiKey = abiKey;
            PackageVersion = packageVersion;
            Platform = platform;
            NativeRuntime = nativeRuntime;
            ProvidersShared = providersShared;
        }

        public string AbiKey { get; }
        public string PackageVersion { get; }
        public string Platform { get; }
        public RuntimeFileManifest NativeRuntime { get; }
        public RuntimeFileManifest ProvidersShared { get; }

        public bool Matches(RuntimeManifest other)
        {
            return string.Equals(AbiKey, other.AbiKey, StringComparison.Ordinal)
                   && string.Equals(PackageVersion, other.PackageVersion, StringComparison.Ordinal)
                   && string.Equals(Platform, other.Platform, StringComparison.Ordinal)
                   && NativeRuntime.Matches(other.NativeRuntime)
                   && ProvidersShared.Matches(other.ProvidersShared);
        }

        public static RuntimeManifest? Parse(string json, out string error)
        {
            error = string.Empty;

            string abiKey = ReadJsonString(json, "abiKey");
            string packageVersion = ReadJsonString(json, "onnxRuntimePackageVersion");
            string platform = ReadJsonString(json, "platform");
            RuntimeFileManifest? nativeRuntime = RuntimeFileManifest.Parse(json, NativeRuntimeResourceName);
            RuntimeFileManifest? providersShared = RuntimeFileManifest.Parse(json, ProvidersSharedResourceName);

            if (string.IsNullOrWhiteSpace(abiKey))
                error = "abi-key-missing";
            else if (string.IsNullOrWhiteSpace(packageVersion))
                error = "package-version-missing";
            else if (string.IsNullOrWhiteSpace(platform))
                error = "platform-missing";
            else if (nativeRuntime == null)
                error = "onnxruntime-file-entry-missing";
            else if (providersShared == null)
                error = "providers-shared-file-entry-missing";

            if (!string.IsNullOrEmpty(error))
                return null;

            return new RuntimeManifest(SanitizePathSegment(abiKey), packageVersion, platform, nativeRuntime!, providersShared!);
        }
    }

    private sealed class RuntimeFileManifest
    {
        private RuntimeFileManifest(string name, long length, string sha256)
        {
            Name = name;
            Length = length;
            Sha256 = sha256;
        }

        public string Name { get; }
        public long Length { get; }
        public string Sha256 { get; }

        public bool Matches(RuntimeFileManifest other)
        {
            return string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
                   && Length == other.Length
                   && string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        public static RuntimeFileManifest? Parse(string json, string fileName)
        {
            int nameIndex = json.IndexOf("\"" + fileName + "\"", StringComparison.OrdinalIgnoreCase);
            if (nameIndex < 0)
                return null;

            int objectStart = json.LastIndexOf('{', nameIndex);
            int objectEnd = json.IndexOf('}', nameIndex);
            if (objectStart < 0 || objectEnd <= objectStart)
                return null;

            string block = json.Substring(objectStart, objectEnd - objectStart + 1);
            long length = ReadJsonLong(block, "length");
            string sha256 = ReadJsonString(block, "sha256");
            if (length <= 0 || string.IsNullOrWhiteSpace(sha256))
                return null;

            return new RuntimeFileManifest(fileName, length, sha256);
        }
    }

    private static string ReadJsonString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
            return string.Empty;

        string token = "\"" + propertyName + "\"";
        int propertyIndex = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (propertyIndex < 0)
            return string.Empty;

        int colon = json.IndexOf(':', propertyIndex + token.Length);
        if (colon < 0)
            return string.Empty;

        int firstQuote = json.IndexOf('"', colon + 1);
        if (firstQuote < 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (int i = firstQuote + 1; i < json.Length; i++)
        {
            char ch = json[i];
            if (ch == '"')
                return builder.ToString();

            if (ch == '\\' && i + 1 < json.Length)
            {
                i++;
                builder.Append(json[i]);
                continue;
            }

            builder.Append(ch);
        }

        return string.Empty;
    }

    private static long ReadJsonLong(string json, string propertyName)
    {
        string token = "\"" + propertyName + "\"";
        int propertyIndex = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (propertyIndex < 0)
            return -1;

        int colon = json.IndexOf(':', propertyIndex + token.Length);
        if (colon < 0)
            return -1;

        int start = colon + 1;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;

        int end = start;
        while (end < json.Length && char.IsDigit(json[end]))
            end++;

        return long.TryParse(json.Substring(start, end - start), out long value) ? value : -1;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value.Trim())
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        return builder.ToString();
    }
}
