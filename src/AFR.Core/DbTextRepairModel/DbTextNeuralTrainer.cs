using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace AFR.DbTextRepairModel;

internal static class DbTextNeuralTrainer
{
    private const int Hidden1Size = 32;
    private const int Hidden2Size = 8;
    private const int Epochs = 240;
    private const float LearningRate = 0.035f;

    public static DbTextNeuralTrainingResult Train(IReadOnlyList<DbTextRepairModelRecord> labels, string sourceSetId)
    {
        List<TrainingExample> examples = BuildExamples(labels);
        if (examples.Count == 0)
            return DbTextNeuralTrainingResult.Fail("没有可训练的 DBText 标签。");

        int inputSize = DbTextNeuralFeatureExtractor.FeatureCount;
        int weightCount = inputSize * Hidden1Size + Hidden1Size * Hidden2Size + Hidden2Size;
        int biasCount = Hidden1Size + Hidden2Size + 1;
        float[] weights = InitializeWeights(weightCount);
        float[] biases = new float[biasCount];
        var network = new MutableNetwork(inputSize, Hidden1Size, Hidden2Size, weights, biases);

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            foreach (TrainingExample example in examples)
                network.TrainOne(example.Features, example.Label, LearningRate);
        }

        EvaluationSummary summary = Evaluate(examples, network);
        string trainingDataHash = DbTextRepairModelJsonl.ComputeTrainingDataHash(labels);
        string architectureJson = JsonConvert.SerializeObject(
            new { input = inputSize, hidden1 = Hidden1Size, hidden2 = Hidden2Size, output = 1 },
            Formatting.None);
        string validationJson = JsonConvert.SerializeObject(summary, Formatting.None);

        string parameterBasis = string.Join(
            "\u001E",
            DbTextRepairModelConstants.NeuralModelKind,
            DbTextRepairModelConstants.NeuralFeatureSchemaVersion,
            trainingDataHash,
            architectureJson,
            DbTextNeuralRanker.EncodeFloats(weights),
            DbTextNeuralRanker.EncodeFloats(biases));

        var record = new DbTextRepairModelRecord
        {
            RecordType = DbTextRepairModelConstants.RecordTypeNeuralParameters,
            RecordId = "nn-params-" + DbTextRepairModelJsonl.ComputeTextHash(parameterBasis).Substring(0, 24),
            SourceSetId = string.IsNullOrEmpty(sourceSetId) ? "local-train" : sourceSetId,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            ModelKind = DbTextRepairModelConstants.NeuralModelKind,
            FeatureSchemaVersion = DbTextRepairModelConstants.NeuralFeatureSchemaVersion,
            ArchitectureJson = architectureJson,
            WeightsBase64 = DbTextNeuralRanker.EncodeFloats(weights),
            BiasBase64 = DbTextNeuralRanker.EncodeFloats(biases),
            TrainingDataHash = trainingDataHash,
            TrainingRecordCount = labels.Count(r => r.IsLabel),
            ValidationSummaryJson = validationJson,
            Note = "Local deterministic DBText MLP parameters."
        };

        return DbTextNeuralTrainingResult.Ok(record, summary);
    }

    public static string Evaluate(IReadOnlyList<DbTextRepairModelRecord> labels, DbTextNeuralRanker ranker)
    {
        List<TrainingExample> examples = BuildExamples(labels);
        if (examples.Count == 0)
            return "没有可评估的 DBText 标签。";

        int correct = 0;
        int falsePositive = 0;
        int falseNegative = 0;
        foreach (TrainingExample example in examples)
        {
            float score = ranker.Score(example.Features);
            bool predicted = score >= 0.5f;
            bool expected = example.Label >= 0.5f;
            if (predicted == expected)
                correct++;
            else if (predicted)
                falsePositive++;
            else
                falseNegative++;
        }

        double accuracy = correct / (double)Math.Max(1, examples.Count);
        return string.Format(
            CultureInfo.InvariantCulture,
            "Examples={0}, Accuracy={1:P2}, FalsePositive={2}, FalseNegative={3}",
            examples.Count,
            accuracy,
            falsePositive,
            falseNegative);
    }

    private static List<TrainingExample> BuildExamples(IReadOnlyList<DbTextRepairModelRecord> labels)
    {
        var examples = new List<TrainingExample>();
        foreach (DbTextRepairModelRecord label in labels
                     .Where(r => r.IsLabel)
                     .OrderBy(DbTextRepairModelJsonl.GetRepairKey, StringComparer.Ordinal)
                     .ThenBy(r => r.RecordId, StringComparer.Ordinal))
        {
            string current = label.CurrentText ?? string.Empty;
            string selected = label.SelectedText ?? string.Empty;
            string candidate = label.CandidateText ?? string.Empty;
            if (string.IsNullOrEmpty(current))
                continue;

            if (string.Equals(label.Action, DbTextRepairModelConstants.ActionRepair, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(selected))
                    examples.Add(BuildExample(label, selected, "label-selected", 1f));

                if (!string.Equals(current, selected, StringComparison.Ordinal))
                    examples.Add(BuildExample(label, current, "current-noop", 0f));

                if (!string.IsNullOrEmpty(candidate)
                    && !string.Equals(candidate, selected, StringComparison.Ordinal))
                    examples.Add(BuildExample(label, candidate, "label-candidate-negative", 0f));
                continue;
            }

            if (string.Equals(label.Action, DbTextRepairModelConstants.ActionKeep, StringComparison.OrdinalIgnoreCase)
                || string.Equals(label.Action, DbTextRepairModelConstants.ActionGlyphIssue, StringComparison.OrdinalIgnoreCase))
            {
                examples.Add(BuildExample(label, current, "current-noop", 1f));
                if (!string.IsNullOrEmpty(candidate)
                    && !string.Equals(candidate, current, StringComparison.Ordinal))
                    examples.Add(BuildExample(label, candidate, "blocked-candidate", 0f));
            }
        }

        return examples;
    }

    private static TrainingExample BuildExample(DbTextRepairModelRecord label, string candidate, string source, float expected)
    {
        return new TrainingExample(
            DbTextNeuralFeatureExtractor.Extract(label, candidate, source),
            expected);
    }

    private static float[] InitializeWeights(int count)
    {
        var values = new float[count];
        var random = new Random(1729);
        for (int i = 0; i < values.Length; i++)
            values[i] = (float)((random.NextDouble() - 0.5) * 0.12);
        return values;
    }

    private static EvaluationSummary Evaluate(List<TrainingExample> examples, MutableNetwork network)
    {
        int positive = examples.Count(e => e.Label >= 0.5f);
        int negative = examples.Count - positive;
        int correct = 0;
        int falsePositive = 0;
        int falseNegative = 0;

        foreach (TrainingExample example in examples)
        {
            float score = network.Score(example.Features);
            bool predicted = score >= 0.5f;
            bool expected = example.Label >= 0.5f;
            if (predicted == expected)
                correct++;
            else if (predicted)
                falsePositive++;
            else
                falseNegative++;
        }

        return new EvaluationSummary
        {
            Examples = examples.Count,
            Positive = positive,
            Negative = negative,
            Accuracy = correct / (double)Math.Max(1, examples.Count),
            FalsePositive = falsePositive,
            FalseNegative = falseNegative,
            Epochs = Epochs,
            LearningRate = LearningRate
        };
    }

    private sealed class MutableNetwork
    {
        private readonly int _inputSize;
        private readonly int _hidden1Size;
        private readonly int _hidden2Size;
        private readonly float[] _weights;
        private readonly float[] _biases;
        private readonly float[] _hidden1;
        private readonly float[] _hidden2;
        private readonly float[] _hidden1Grad;
        private readonly float[] _hidden2Grad;

        public MutableNetwork(int inputSize, int hidden1Size, int hidden2Size, float[] weights, float[] biases)
        {
            _inputSize = inputSize;
            _hidden1Size = hidden1Size;
            _hidden2Size = hidden2Size;
            _weights = weights;
            _biases = biases;
            _hidden1 = new float[hidden1Size];
            _hidden2 = new float[hidden2Size];
            _hidden1Grad = new float[hidden1Size];
            _hidden2Grad = new float[hidden2Size];
        }

        public float Score(float[] features)
        {
            int w1 = 0;
            int w2 = _hidden1Size * _inputSize;
            int w3 = w2 + _hidden2Size * _hidden1Size;
            int b1 = 0;
            int b2 = _hidden1Size;
            int b3 = b2 + _hidden2Size;

            for (int h = 0; h < _hidden1Size; h++)
            {
                float sum = _biases[b1 + h];
                for (int i = 0; i < _inputSize; i++)
                    sum += features[i] * _weights[w1 + h * _inputSize + i];
                _hidden1[h] = Relu(sum);
            }

            for (int h = 0; h < _hidden2Size; h++)
            {
                float sum = _biases[b2 + h];
                for (int i = 0; i < _hidden1Size; i++)
                    sum += _hidden1[i] * _weights[w2 + h * _hidden1Size + i];
                _hidden2[h] = Relu(sum);
            }

            float output = _biases[b3];
            for (int i = 0; i < _hidden2Size; i++)
                output += _hidden2[i] * _weights[w3 + i];

            return Sigmoid(output);
        }

        public void TrainOne(float[] features, float expected, float learningRate)
        {
            float output = Score(features);
            float outputGrad = output - expected;

            int w1 = 0;
            int w2 = _hidden1Size * _inputSize;
            int w3 = w2 + _hidden2Size * _hidden1Size;
            int b1 = 0;
            int b2 = _hidden1Size;
            int b3 = b2 + _hidden2Size;

            for (int h = 0; h < _hidden2Size; h++)
                _hidden2Grad[h] = outputGrad * _weights[w3 + h] * ReluGrad(_hidden2[h]);

            for (int h = 0; h < _hidden1Size; h++)
            {
                float sum = 0;
                for (int j = 0; j < _hidden2Size; j++)
                    sum += _hidden2Grad[j] * _weights[w2 + j * _hidden1Size + h];
                _hidden1Grad[h] = sum * ReluGrad(_hidden1[h]);
            }

            for (int h = 0; h < _hidden2Size; h++)
                _weights[w3 + h] -= learningRate * outputGrad * _hidden2[h];
            _biases[b3] -= learningRate * outputGrad;

            for (int h = 0; h < _hidden2Size; h++)
            {
                for (int i = 0; i < _hidden1Size; i++)
                    _weights[w2 + h * _hidden1Size + i] -= learningRate * _hidden2Grad[h] * _hidden1[i];
                _biases[b2 + h] -= learningRate * _hidden2Grad[h];
            }

            for (int h = 0; h < _hidden1Size; h++)
            {
                for (int i = 0; i < _inputSize; i++)
                    _weights[w1 + h * _inputSize + i] -= learningRate * _hidden1Grad[h] * features[i];
                _biases[b1 + h] -= learningRate * _hidden1Grad[h];
            }
        }

        private static float Relu(float value) => value > 0 ? value : 0;

        private static float ReluGrad(float activatedValue) => activatedValue > 0 ? 1f : 0f;

        private static float Sigmoid(float value)
        {
            if (value > 20)
                return 1;
            if (value < -20)
                return 0;

            return (float)(1.0 / (1.0 + Math.Exp(-value)));
        }
    }

    private sealed class TrainingExample
    {
        public TrainingExample(float[] features, float label)
        {
            Features = features;
            Label = label;
        }

        public float[] Features { get; }
        public float Label { get; }
    }

    private sealed class EvaluationSummary
    {
        public int Examples { get; set; }
        public int Positive { get; set; }
        public int Negative { get; set; }
        public double Accuracy { get; set; }
        public int FalsePositive { get; set; }
        public int FalseNegative { get; set; }
        public int Epochs { get; set; }
        public float LearningRate { get; set; }
    }
}

internal sealed class DbTextNeuralTrainingResult
{
    private DbTextNeuralTrainingResult(bool success, DbTextRepairModelRecord? record, string error, string summary)
    {
        Success = success;
        Record = record;
        Error = error;
        Summary = summary;
    }

    public bool Success { get; }
    public DbTextRepairModelRecord? Record { get; }
    public string Error { get; }
    public string Summary { get; }

    public static DbTextNeuralTrainingResult Ok(DbTextRepairModelRecord record, object summary)
    {
        return new DbTextNeuralTrainingResult(true, record, string.Empty, JsonConvert.SerializeObject(summary, Formatting.None));
    }

    public static DbTextNeuralTrainingResult Fail(string error)
    {
        return new DbTextNeuralTrainingResult(false, null, error, string.Empty);
    }
}
