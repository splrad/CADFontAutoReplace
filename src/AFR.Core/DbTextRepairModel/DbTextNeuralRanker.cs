using System;
using Newtonsoft.Json.Linq;

namespace AFR.DbTextRepairModel;

internal sealed class DbTextNeuralRanker
{
    private readonly int _inputSize;
    private readonly int _hidden1Size;
    private readonly int _hidden2Size;
    private readonly float[] _weights;
    private readonly float[] _biases;
    private readonly float[] _hidden1;
    private readonly float[] _hidden2;

    private DbTextNeuralRanker(int inputSize, int hidden1Size, int hidden2Size, float[] weights, float[] biases)
    {
        _inputSize = inputSize;
        _hidden1Size = hidden1Size;
        _hidden2Size = hidden2Size;
        _weights = weights;
        _biases = biases;
        _hidden1 = new float[hidden1Size];
        _hidden2 = new float[hidden2Size];
    }

    public string TrainingDataHash { get; private init; } = string.Empty;
    public string ValidationSummaryJson { get; private init; } = string.Empty;

    public float Score(DbTextRepairModelRecord context, string candidateText, string candidateSource)
    {
        float[] features = DbTextNeuralFeatureExtractor.Extract(context, candidateText, candidateSource);
        return Score(features);
    }

    public float Score(float[] features)
    {
        if (features.Length != _inputSize)
            return 0;

        int offset = 0;
        int biasOffset = 0;

        for (int h = 0; h < _hidden1Size; h++)
        {
            float sum = _biases[biasOffset + h];
            for (int i = 0; i < _inputSize; i++)
                sum += features[i] * _weights[offset + h * _inputSize + i];
            _hidden1[h] = Relu(sum);
        }

        offset += _hidden1Size * _inputSize;
        biasOffset += _hidden1Size;

        for (int h = 0; h < _hidden2Size; h++)
        {
            float sum = _biases[biasOffset + h];
            for (int i = 0; i < _hidden1Size; i++)
                sum += _hidden1[i] * _weights[offset + h * _hidden1Size + i];
            _hidden2[h] = Relu(sum);
        }

        offset += _hidden2Size * _hidden1Size;
        biasOffset += _hidden2Size;

        float output = _biases[biasOffset];
        for (int i = 0; i < _hidden2Size; i++)
            output += _hidden2[i] * _weights[offset + i];

        return Sigmoid(output);
    }

    public static bool TryCreate(DbTextRepairModelRecord record, out DbTextNeuralRanker? ranker, out string error)
    {
        ranker = null;
        error = string.Empty;

        try
        {
            if (!record.IsNeuralParameters)
            {
                error = "record-not-nn-params";
                return false;
            }

            if (!string.Equals(record.ModelKind, DbTextRepairModelConstants.NeuralModelKind, StringComparison.Ordinal)
                || !string.Equals(record.FeatureSchemaVersion, DbTextRepairModelConstants.NeuralFeatureSchemaVersion, StringComparison.Ordinal))
            {
                error = "unsupported-model";
                return false;
            }

            JObject architecture = JObject.Parse(record.ArchitectureJson);
            int input = architecture.Value<int>("input");
            int hidden1 = architecture.Value<int>("hidden1");
            int hidden2 = architecture.Value<int>("hidden2");
            int output = architecture.Value<int>("output");

            if (input != DbTextNeuralFeatureExtractor.FeatureCount || hidden1 <= 0 || hidden2 <= 0 || output != 1)
            {
                error = "invalid-architecture";
                return false;
            }

            float[] weights = DecodeFloats(record.WeightsBase64);
            float[] biases = DecodeFloats(record.BiasBase64);
            int expectedWeights = input * hidden1 + hidden1 * hidden2 + hidden2;
            int expectedBiases = hidden1 + hidden2 + 1;
            if (weights.Length != expectedWeights || biases.Length != expectedBiases)
            {
                error = "parameter-size-mismatch";
                return false;
            }

            ranker = new DbTextNeuralRanker(input, hidden1, hidden2, weights, biases)
            {
                TrainingDataHash = record.TrainingDataHash,
                ValidationSummaryJson = record.ValidationSummaryJson
            };
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    internal static string EncodeFloats(float[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            byte[] valueBytes = BitConverter.GetBytes(values[i]);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(valueBytes);
            Buffer.BlockCopy(valueBytes, 0, bytes, i * 4, 4);
        }

        return Convert.ToBase64String(bytes);
    }

    private static float[] DecodeFloats(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return Array.Empty<float>();

        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length % 4 != 0)
            return Array.Empty<float>();

        var values = new float[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            var valueBytes = new byte[4];
            Buffer.BlockCopy(bytes, i * 4, valueBytes, 0, 4);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(valueBytes);
            values[i] = BitConverter.ToSingle(valueBytes, 0);
        }

        return values;
    }

    private static float Relu(float value) => value > 0 ? value : 0;

    private static float Sigmoid(float value)
    {
        if (value > 20)
            return 1;
        if (value < -20)
            return 0;

        return (float)(1.0 / (1.0 + Math.Exp(-value)));
    }
}
