using System;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairConstants
{
    public const string FeatureSchemaVersion = "dbtext-ai-features-v1";
    public const string ModelResourceName = "AFR.GlyphCore.Model.onnx";
    public const string ModelManifestResourceName = "AFR.GlyphCore.ModelManifest.json";
    public const string ExactRepairsResourceName = "AFR.GlyphCore.ExactRepairs.json";
    public const string RuntimeManagedResourceName = "Microsoft.ML.OnnxRuntime.dll";
    public const string RuntimeNativeResourceName = "onnxruntime.dll";
    public const string RuntimeProvidersSharedResourceName = "onnxruntime_providers_shared.dll";
    public const float MinimumConfidence = 0.92f;
    public const float MinimumScoreMargin = 0.18f;
}

internal sealed class GlyphCoreTextRepairContext
{
    public string DrawingPath { get; set; } = string.Empty;
    public string DrawingFileName { get; set; } = string.Empty;
    public long DrawingLength { get; set; }
    public string DrawingLastWriteUtc { get; set; } = string.Empty;
    public string DrawingSha256 { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string OwnerBlockName { get; set; } = string.Empty;
    public string TextStyleName { get; set; } = string.Empty;
    public string TextStyleFileName { get; set; } = string.Empty;
    public string TextStyleBigFontFileName { get; set; } = string.Empty;
    public string TextStyleTypeFace { get; set; } = string.Empty;
    public string CurrentText { get; set; } = string.Empty;
    public bool IsFromExternalReference { get; set; }
}

internal sealed class GlyphCoreTextRepairCandidate
{
    public GlyphCoreTextRepairCandidate(string text, string source, string reason, bool isRoundTrip)
    {
        Text = text ?? string.Empty;
        Source = source ?? string.Empty;
        Reason = reason ?? string.Empty;
        IsRoundTrip = isRoundTrip;
    }

    public string Text { get; }
    public string Source { get; private set; }
    public string Reason { get; private set; }
    public bool IsRoundTrip { get; private set; }
    public bool HasAiScore { get; private set; }
    public float AiScore { get; private set; }
    public bool HasAiScoreError { get; private set; }
    public string AiScoreError { get; private set; } = string.Empty;

    public bool IsNoOp => Source.IndexOf("current-noop", StringComparison.OrdinalIgnoreCase) >= 0;

    public void AddSource(string source, string reason, bool isRoundTrip)
    {
        if (!string.IsNullOrEmpty(source) && Source.IndexOf(source, StringComparison.OrdinalIgnoreCase) < 0)
            Source += "+" + source;
        if (!string.IsNullOrEmpty(reason) && Reason.IndexOf(reason, StringComparison.OrdinalIgnoreCase) < 0)
            Reason += "; " + reason;
        IsRoundTrip = IsRoundTrip && isRoundTrip;
    }

    public void SetAiScore(float score)
    {
        AiScore = score;
        HasAiScore = true;
        AiScoreError = string.Empty;
        HasAiScoreError = false;
    }

    public void SetAiScoreError(string error)
    {
        AiScoreError = error ?? string.Empty;
        HasAiScoreError = !string.IsNullOrWhiteSpace(AiScoreError);
    }
}

internal interface IGlyphCoreTextRepairScorer
{
    bool IsAvailable { get; }
    string Status { get; }
    bool TryScore(GlyphCoreTextRepairContext context, GlyphCoreTextRepairCandidate candidate, float[] features, out float score, out string error);
}

internal sealed class GlyphCoreTextRepairUnavailableScorer : IGlyphCoreTextRepairScorer
{
    public GlyphCoreTextRepairUnavailableScorer(string status)
    {
        Status = string.IsNullOrWhiteSpace(status) ? "unavailable" : status;
    }

    public bool IsAvailable => false;
    public string Status { get; }

    public bool TryScore(GlyphCoreTextRepairContext context, GlyphCoreTextRepairCandidate candidate, float[] features, out float score, out string error)
    {
        score = 0;
        error = Status;
        return false;
    }
}

internal sealed class GlyphCoreTextRepairDecision
{
    private GlyphCoreTextRepairDecision(string action, string selectedText, string reason, string aiSummary)
    {
        Action = action;
        SelectedText = selectedText;
        Reason = reason;
        AiSummary = aiSummary;
    }

    public string Action { get; }
    public string SelectedText { get; }
    public string Reason { get; }
    public string AiSummary { get; }

    public bool ShouldRepair => string.Equals(Action, "repair", StringComparison.Ordinal);
    public bool IsBlocked => string.Equals(Action, "unsafe", StringComparison.Ordinal);

    public static GlyphCoreTextRepairDecision Repair(string selectedText, string reason, string aiSummary) =>
        new("repair", selectedText, reason, aiSummary);

    public static GlyphCoreTextRepairDecision Unsafe(string reason, string aiSummary) =>
        new("unsafe", string.Empty, reason, aiSummary);

    public static GlyphCoreTextRepairDecision Skip(string reason, string aiSummary) =>
        new("skip", string.Empty, reason, aiSummary);
}

