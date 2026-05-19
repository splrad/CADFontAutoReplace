using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairRuntimeResources
{
    private const string RuntimeRootDirectoryName = "OnnxRuntime";
    private const string RuntimeManifestResourceName = "AFR.GlyphCore.OnnxRuntimeManifest.json";
    private const string RuntimeManifestFileName = "AFR.OnnxRuntime.manifest.json";

    public static bool EnsureNativeRuntimeExtracted(Assembly owner, out string error)
    {
        error = string.Empty;

        string embeddedManifestText = ReadTextResource(owner, RuntimeManifestResourceName) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(embeddedManifestText))
        {
            error = "onnx-runtime-manifest-resource-missing";
            return false;
        }

        RuntimeManifest? manifest = RuntimeManifest.Parse(embeddedManifestText, out string manifestError);
        if (manifest == null)
        {
            error = "onnx-runtime-manifest-invalid: " + manifestError;
            return false;
        }

        string? assemblyDirectory = GetAssemblyDirectory(owner);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            error = "onnx-runtime-plugin-directory-unavailable";
            return false;
        }

        string runtimeDirectory = Path.Combine(assemblyDirectory, RuntimeRootDirectoryName, manifest.AbiKey);
        if (TryGetLoadedOnnxRuntimePath(out string loadedRuntimePath) &&
            !IsSameOrUnderDirectory(loadedRuntimePath, runtimeDirectory))
        {
            error = "onnxruntime-abi-conflict: loaded=" + loadedRuntimePath + "; expected=" + runtimeDirectory;
            return false;
        }

        try
        {
            if (IsRuntimeDirectoryCurrent(runtimeDirectory, manifest))
            {
                PrependPath(runtimeDirectory);
                return true;
            }

            byte[]? nativeRuntime = ReadResource(owner, GlyphCoreTextRepairConstants.RuntimeNativeResourceName);
            if (nativeRuntime == null || nativeRuntime.Length == 0)
            {
                error = "onnx-native-runtime-resource-missing";
                return false;
            }

            byte[]? providersShared = ReadResource(owner, GlyphCoreTextRepairConstants.RuntimeProvidersSharedResourceName);
            if (providersShared == null || providersShared.Length == 0)
            {
                error = "onnx-providers-shared-resource-missing";
                return false;
            }

            return TryExtractNativeRuntime(runtimeDirectory, manifest, embeddedManifestText, nativeRuntime, providersShared, out error);
        }
        catch (Exception ex)
        {
            error = "onnx-native-runtime-extract-failed: " + ex.GetType().Name + ": " + ex.Message;
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

    private static bool TryExtractNativeRuntime(
        string runtimeDirectory,
        RuntimeManifest manifest,
        string embeddedManifestText,
        byte[] nativeRuntime,
        byte[] providersShared,
        out string error)
    {
        error = string.Empty;

        try
        {
            Directory.CreateDirectory(runtimeDirectory);
            WriteIfMismatch(Path.Combine(runtimeDirectory, manifest.NativeRuntime.Name), nativeRuntime, manifest.NativeRuntime);
            WriteIfMismatch(Path.Combine(runtimeDirectory, manifest.ProvidersShared.Name), providersShared, manifest.ProvidersShared);
            WriteTextIfChanged(Path.Combine(runtimeDirectory, RuntimeManifestFileName), embeddedManifestText);
            PrependPath(runtimeDirectory);
            return true;
        }
        catch (Exception ex)
        {
            error = "onnx-native-runtime-extract-failed: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static byte[]? ReadResource(Assembly assembly, string resourceName)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        var data = new byte[stream.Length];
        int offset = 0;
        while (offset < data.Length)
        {
            int read = stream.Read(data, offset, data.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }

        return data;
    }

    private static string? ReadTextResource(Assembly assembly, string resourceName)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    private static string? GetAssemblyDirectory(Assembly assembly)
    {
        try
        {
            string location = assembly.Location;
            if (!string.IsNullOrWhiteSpace(location))
                return Path.GetDirectoryName(location);
        }
        catch
        {
            // The AI scorer will be marked unavailable by the caller.
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

    private static bool TryGetLoadedOnnxRuntimePath(out string loadedRuntimePath)
    {
        loadedRuntimePath = string.Empty;

        try
        {
            using Process process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, GlyphCoreTextRepairConstants.RuntimeNativeResourceName, StringComparison.OrdinalIgnoreCase))
                {
                    loadedRuntimePath = module.FileName;
                    return !string.IsNullOrWhiteSpace(loadedRuntimePath);
                }
            }
        }
        catch
        {
            // Process module enumeration can fail under restrictive hosts. Do not block non-AI startup on inspection.
        }

        return false;
    }

    private static bool IsSameOrUnderDirectory(string path, string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void PrependPath(string directory)
    {
        string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] existing = current.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(directory);

        foreach (string item in existing)
        {
            if (string.Equals(item.Trim(), directory, StringComparison.OrdinalIgnoreCase))
                continue;

            builder.Append(Path.PathSeparator);
            builder.Append(item);
        }

        Environment.SetEnvironmentVariable("PATH", builder.ToString());
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
            RuntimeFileManifest? nativeRuntime = RuntimeFileManifest.Parse(json, GlyphCoreTextRepairConstants.RuntimeNativeResourceName);
            RuntimeFileManifest? providersShared = RuntimeFileManifest.Parse(json, GlyphCoreTextRepairConstants.RuntimeProvidersSharedResourceName);

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
