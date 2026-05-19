using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairRuntimeResources
{
    public static bool EnsureNativeRuntimeExtracted(Assembly owner, out string error)
    {
        error = string.Empty;

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

        try
        {
            string cacheKey = GetNativeRuntimeCacheKey(owner, nativeRuntime, providersShared);
            string localCacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CADFontAutoReplace",
                "GlyphCore",
                "TextRepair",
                "OnnxRuntime",
                cacheKey);

            string? pluginCacheDir = GetPluginNativeRuntimeCacheDirectory(owner, cacheKey);
            string pluginError = string.Empty;
            if (pluginCacheDir != null && pluginCacheDir.Length > 0 &&
                TryExtractNativeRuntime(pluginCacheDir, nativeRuntime, providersShared, out pluginError))
            {
                return true;
            }

            if (TryExtractNativeRuntime(localCacheDir, nativeRuntime, providersShared, out string localError))
                return true;

            error = string.IsNullOrEmpty(pluginCacheDir)
                ? localError
                : pluginError + "; fallback: " + localError;
            return false;
        }
        catch (Exception ex)
        {
            error = "onnx-native-runtime-extract-failed: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool TryExtractNativeRuntime(string cacheDir, byte[] nativeRuntime, byte[] providersShared, out string error)
    {
        error = string.Empty;

        try
        {
            Directory.CreateDirectory(cacheDir);
            WriteIfChanged(Path.Combine(cacheDir, GlyphCoreTextRepairConstants.RuntimeNativeResourceName), nativeRuntime);
            WriteIfChanged(Path.Combine(cacheDir, GlyphCoreTextRepairConstants.RuntimeProvidersSharedResourceName), providersShared);
            PrependPath(cacheDir);
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

    private static string? GetPluginNativeRuntimeCacheDirectory(Assembly owner, string cacheKey)
    {
        string? assemblyDirectory = GetAssemblyDirectory(owner);
        return string.IsNullOrEmpty(assemblyDirectory)
            ? null
            : Path.Combine(assemblyDirectory, "OnnxRuntime", cacheKey);
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
            // Fall back to per-user cache.
        }

        return null;
    }

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
                // Re-write below.
            }
        }

        File.WriteAllBytes(path, data);
    }

    private static string GetNativeRuntimeCacheKey(Assembly owner, byte[] nativeRuntime, byte[] providersShared)
    {
        string version = owner.GetName().Version?.ToString()
            ?? "unknown";
        string key = version + "-" + ShortHash(nativeRuntime) + ShortHash(providersShared);

        foreach (char invalid in Path.GetInvalidFileNameChars())
            key = key.Replace(invalid, '_');

        return key;
    }

    private static string ShortHash(byte[] data)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(data);
        return BitConverter.ToString(hash, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void PrependPath(string directory)
    {
        string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (current.IndexOf(directory, StringComparison.OrdinalIgnoreCase) >= 0)
            return;

        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);
    }
}
