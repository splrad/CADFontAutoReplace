using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AFR.GlyphCore.TextRepair;

internal sealed class GlyphCoreTextRepairEmbeddedOnnxScorer : IGlyphCoreTextRepairScorer
{
    private readonly InferenceSession _session;
    private readonly string _inputName;

    private GlyphCoreTextRepairEmbeddedOnnxScorer(InferenceSession session, string inputName)
    {
        _session = session;
        _inputName = inputName;
    }

    public bool IsAvailable => true;
    public string Status => "onnx-loaded";

    public bool TryScore(GlyphCoreTextRepairContext context, GlyphCoreTextRepairCandidate candidate, float[] features, out float score, out string error)
    {
        score = 0;
        error = string.Empty;

        try
        {
            var tensor = new DenseTensor<float>(features.AsMemory(), new[] { 1, features.Length });
            NamedOnnxValue namedValue = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(new[] { namedValue });
            if (TryReadFirstFloat(results, out score))
                return true;

            error = "onnx-output-empty";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool TryCreate(out IGlyphCoreTextRepairScorer scorer, out string error)
    {
        scorer = new GlyphCoreTextRepairUnavailableScorer("not-created");
        error = string.Empty;

        try
        {
            Assembly owner = typeof(GlyphCoreTextRepairEmbeddedOnnxScorer).Assembly;
            byte[]? modelBytes = ReadResource(owner, GlyphCoreTextRepairConstants.ModelResourceName);
            if (modelBytes == null || modelBytes.Length == 0)
            {
                error = "onnx-model-resource-missing";
                return false;
            }

            string manifestText = ReadTextResource(owner, GlyphCoreTextRepairConstants.ModelManifestResourceName) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(manifestText))
            {
                error = "onnx-model-manifest-missing";
                return false;
            }

            string featureSchema = ReadManifestValue(manifestText, "featureSchemaVersion");
            if (!string.Equals(featureSchema, GlyphCoreTextRepairConstants.FeatureSchemaVersion, StringComparison.Ordinal))
            {
                error = "onnx-feature-schema-mismatch";
                return false;
            }

            if (!EnsureNativeRuntimeExtracted(owner, out error))
                return false;

            InferenceSession session;
            try
            {
                session = new InferenceSession(modelBytes);
            }
            catch (Exception ex)
            {
                error = "onnx-session-create-failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            string inputName = ReadManifestValue(manifestText, "inputName");
            if (string.IsNullOrWhiteSpace(inputName))
                inputName = "features";

            scorer = new GlyphCoreTextRepairEmbeddedOnnxScorer(session, inputName);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool TryReadFirstFloat(object? results, out float score)
    {
        score = 0;
        if (results is not IEnumerable enumerable)
            return false;

        foreach (object? result in enumerable)
        {
            if (result == null)
                continue;

            object? value = result.GetType().GetProperty("Value")?.GetValue(result, null);
            if (value is IEnumerable values)
            {
                foreach (object? item in values)
                {
                    if (item is float f)
                    {
                        score = f;
                        return true;
                    }
                    if (item is double d)
                    {
                        score = (float)d;
                        return true;
                    }
                }
            }
        }

        return false;
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

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool EnsureNativeRuntimeExtracted(Assembly owner, out string error)
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

    private static string? GetPluginNativeRuntimeCacheDirectory(Assembly owner, string cacheKey)
    {
        string? assemblyDirectory = GetAssemblyDirectory(owner);
        return string.IsNullOrEmpty(assemblyDirectory)
            ? null
            : Path.Combine(assemblyDirectory, "GlyphCoreRuntime", "TextRepair", "OnnxRuntime", cacheKey);
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

    private static string ReadManifestValue(string json, string propertyName)
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

        int secondQuote = json.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0)
            return string.Empty;

        return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }
}

