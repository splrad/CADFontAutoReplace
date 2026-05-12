using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using AFR.Services;

namespace AFR.DbTextAI;

internal sealed class DbTextEmbeddedOnnxScorer : IDbTextAiScorer
{
    private readonly object _session;
    private readonly MethodInfo _runMethod;
    private readonly string _inputName;

    private DbTextEmbeddedOnnxScorer(object session, MethodInfo runMethod, string inputName)
    {
        _session = session;
        _runMethod = runMethod;
        _inputName = inputName;
    }

    public bool IsAvailable => true;
    public string Status => "onnx-loaded";

    public bool TryScore(DbTextAiContext context, DbTextAiCandidate candidate, float[] features, out float score, out string error)
    {
        score = 0;
        error = string.Empty;

        try
        {
            Assembly runtimeAssembly = _session.GetType().Assembly;
            Type? denseTensorOpenType = runtimeAssembly.GetType("Microsoft.ML.OnnxRuntime.Tensors.DenseTensor`1");
            Type? namedValueType = runtimeAssembly.GetType("Microsoft.ML.OnnxRuntime.NamedOnnxValue");
            if (denseTensorOpenType == null || namedValueType == null)
            {
                error = "onnx-runtime-types-missing";
                return false;
            }

            Type denseTensorType = denseTensorOpenType.MakeGenericType(typeof(float));
            object? tensor = Activator.CreateInstance(
                denseTensorType,
                new object[] { features, new[] { 1, features.Length } });
            if (tensor == null)
            {
                error = "tensor-create-failed";
                return false;
            }

            MethodInfo? createFromTensor = namedValueType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CreateFromTensor" && m.IsGenericMethodDefinition);
            if (createFromTensor == null)
            {
                error = "named-value-factory-missing";
                return false;
            }

            object? namedValue = createFromTensor
                .MakeGenericMethod(typeof(float))
                .Invoke(null, new[] { _inputName, tensor });
            if (namedValue == null)
            {
                error = "named-value-create-failed";
                return false;
            }

            Array inputArray = Array.CreateInstance(namedValueType, 1);
            inputArray.SetValue(namedValue, 0);
            object? results = _runMethod.Invoke(_session, new object[] { inputArray });
            try
            {
                if (TryReadFirstFloat(results, out score))
                    return true;

                error = "onnx-output-empty";
                return false;
            }
            finally
            {
                if (results is IDisposable disposable)
                    disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool TryCreate(out IDbTextAiScorer scorer, out string error)
    {
        scorer = new DbTextUnavailableAiScorer("not-created");
        error = string.Empty;

        try
        {
            Assembly owner = typeof(DbTextEmbeddedOnnxScorer).Assembly;
            byte[]? modelBytes = ReadResource(owner, DbTextAiConstants.ModelResourceName);
            if (modelBytes == null || modelBytes.Length == 0)
            {
                error = "onnx-model-resource-missing";
                return false;
            }

            string manifestText = ReadTextResource(owner, DbTextAiConstants.ModelManifestResourceName) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(manifestText))
            {
                error = "onnx-model-manifest-missing";
                return false;
            }

            string featureSchema = ReadManifestValue(manifestText, "featureSchemaVersion");
            if (!string.Equals(featureSchema, DbTextAiConstants.FeatureSchemaVersion, StringComparison.Ordinal))
            {
                error = "onnx-feature-schema-mismatch";
                return false;
            }

            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CADFontAutoReplace",
                "DbTextAI",
                PluginVersionService.GetBuildId());
            Directory.CreateDirectory(cacheDir);

            ExtractOptionalResource(owner, DbTextAiConstants.RuntimeNativeResourceName, Path.Combine(cacheDir, DbTextAiConstants.RuntimeNativeResourceName));
            ExtractOptionalResource(owner, DbTextAiConstants.RuntimeManagedResourceName, Path.Combine(cacheDir, DbTextAiConstants.RuntimeManagedResourceName));
            PrependPath(cacheDir);

            string modelPath = Path.Combine(cacheDir, DbTextAiConstants.ModelResourceName);
            WriteIfChanged(modelPath, modelBytes);

            Assembly runtimeAssembly = LoadOnnxRuntime(owner, cacheDir);
            Type? sessionType = runtimeAssembly.GetType("Microsoft.ML.OnnxRuntime.InferenceSession");
            if (sessionType == null)
            {
                error = "onnx-session-type-missing";
                return false;
            }

            object? session = Activator.CreateInstance(sessionType, modelPath);
            if (session == null)
            {
                error = "onnx-session-create-failed";
                return false;
            }

            MethodInfo? runMethod = sessionType.GetMethods()
                .FirstOrDefault(m => m.Name == "Run"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType.IsGenericType);
            if (runMethod == null)
            {
                error = "onnx-run-method-missing";
                return false;
            }

            string inputName = ReadManifestValue(manifestText, "inputName");
            if (string.IsNullOrWhiteSpace(inputName))
                inputName = "features";

            scorer = new DbTextEmbeddedOnnxScorer(session, runMethod, inputName);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static Assembly LoadOnnxRuntime(Assembly owner, string cacheDir)
    {
        string managedPath = Path.Combine(cacheDir, DbTextAiConstants.RuntimeManagedResourceName);
        if (File.Exists(managedPath))
            return Assembly.LoadFrom(managedPath);

        byte[]? embedded = ReadResource(owner, DbTextAiConstants.RuntimeManagedResourceName);
        if (embedded != null && embedded.Length > 0)
            return Assembly.Load(embedded);

        return Assembly.Load("Microsoft.ML.OnnxRuntime");
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

    private static void ExtractOptionalResource(Assembly owner, string resourceName, string path)
    {
        byte[]? data = ReadResource(owner, resourceName);
        if (data == null || data.Length == 0)
            return;

        WriteIfChanged(path, data);
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
