using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AFR.GlyphCore.TextRepair;

namespace AFR.Services.GlyphCore.TextRepair;

/// <summary>
/// In-memory bridge for native DBCS/code-page evidence collected before DBText repair.
/// Future native hooks should register object- or cluster-level records here; Release builds
/// never persist these records to disk.
/// </summary>
internal static class GlyphCoreNativeDecodeEvidenceStore
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, GlyphCoreNativeDecodeEvidenceRecord> ObjectEvidence = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, GlyphCoreNativeDecodeEvidenceRecord> ClusterEvidence = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterDbTextDecodeEvidence(
        string drawingSha256,
        string drawingPath,
        string handle,
        string clusterKey,
        string sourceCodePageFamily,
        string appliedCodePageFamily,
        byte[]? rawPayload,
        string preferredDecodedText,
        bool rawRoundTrip,
        float rawConfidence)
    {
        RegisterDbTextDecodeEvidence(new GlyphCoreDbTextDecodeEvidence
        {
            DrawingSha256 = drawingSha256,
            DrawingPath = drawingPath,
            Handle = handle,
            ClusterKey = clusterKey,
            SourceCodePageFamily = sourceCodePageFamily,
            AppliedCodePageFamily = appliedCodePageFamily,
            RawPayloadSha256 = rawPayload == null || rawPayload.Length == 0 ? string.Empty : Sha256Hex(rawPayload),
            RawPayloadLength = rawPayload?.Length ?? 0,
            PreferredDecodedText = preferredDecodedText,
            RawRoundTrip = rawRoundTrip,
            RawConfidence = rawConfidence
        });
    }

    public static void RegisterDbTextDecodeEvidence(GlyphCoreDbTextDecodeEvidence evidence)
    {
        if (evidence == null)
            return;

        Register(new GlyphCoreNativeDecodeEvidenceRecord
        {
            DrawingSha256 = evidence.DrawingSha256,
            DrawingPath = evidence.DrawingPath,
            Handle = evidence.Handle,
            ClusterKey = evidence.ClusterKey,
            Scope = !string.IsNullOrWhiteSpace(evidence.Scope)
                ? evidence.Scope
                : (!string.IsNullOrWhiteSpace(evidence.Handle) ? "object" : "cluster"),
            SourceCodePageFamily = evidence.SourceCodePageFamily,
            AppliedCodePageFamily = evidence.AppliedCodePageFamily,
            HookHitType = string.IsNullOrWhiteSpace(evidence.HookHitType) ? "dbtext" : evidence.HookHitType,
            ObjectCorrelation = evidence.ObjectCorrelation,
            ClusterCorrelation = evidence.ClusterCorrelation,
            HasHookRawDecodeEvidence = evidence.HasRawPayload || !string.IsNullOrWhiteSpace(evidence.PreferredDecodedText),
            HookRawPayloadSha256 = evidence.RawPayloadSha256,
            HookRawPayloadLength = Math.Max(0, evidence.RawPayloadLength),
            HookPreferredDecodedText = evidence.PreferredDecodedText,
            HookRawCandidateSource = string.IsNullOrWhiteSpace(evidence.CandidateSource)
                ? "hook-raw-stream"
                : evidence.CandidateSource,
            HookRawRoundTrip = evidence.RawRoundTrip,
            HookRawConfidence = Clamp01(evidence.RawConfidence)
        });
    }

    public static void Register(GlyphCoreNativeDecodeEvidenceRecord record)
    {
        if (record == null || !record.HasMismatch)
            return;

        lock (Lock)
        {
            string objectKey = BuildObjectKey(record.DrawingSha256, record.DrawingPath, record.Handle);
            if (!string.IsNullOrEmpty(objectKey))
                ObjectEvidence[objectKey] = record;

            string clusterKey = BuildEvidenceClusterKey(record.DrawingSha256, record.DrawingPath, record.ClusterKey);
            if (!string.IsNullOrEmpty(clusterKey))
                ClusterEvidence[clusterKey] = record;
        }
    }

    public static void ApplyEvidence(GlyphCoreDrawingIdentity drawing, GlyphCoreTextRepairContext context)
    {
        if (context == null)
            return;

        GlyphCoreNativeDecodeEvidenceRecord? record = null;
        string objectKey = BuildObjectKey(drawing.Sha256, drawing.Path, context.Handle);
        string contextClusterKey = BuildContextClusterKey(context);
        string evidenceClusterKey = BuildEvidenceClusterKey(drawing.Sha256, drawing.Path, contextClusterKey);

        lock (Lock)
        {
            if (!string.IsNullOrEmpty(objectKey))
                ObjectEvidence.TryGetValue(objectKey, out record);

            if (record == null && !string.IsNullOrEmpty(evidenceClusterKey))
                ClusterEvidence.TryGetValue(evidenceClusterKey, out record);
        }

        if (record == null)
        {
            context.NativeDecodeEvidenceClusterKey = contextClusterKey;
            return;
        }

        ApplyRecord(context, record, record.HasObjectHandle ? "object" : "cluster", contextClusterKey);
    }

    public static void ApplyRippleEvidence(
        GlyphCoreTextRepairContext context,
        GlyphCoreTextRepairContext seed,
        int seedCount,
        float distanceRatio,
        float seedQuality)
    {
        if (context == null || seed == null)
            return;

        context.HasNativeDecodeEvidence = true;
        context.NativeDecodeFamilyMismatch = seed.NativeDecodeFamilyMismatch;
        context.NativeDecodeEvidenceScope = "ripple";
        context.NativeDecodeEvidenceClusterKey = seed.NativeDecodeEvidenceClusterKey;
        context.NativeDecodeSourceCodePageFamily = seed.NativeDecodeSourceCodePageFamily;
        context.NativeDecodeAppliedCodePageFamily = seed.NativeDecodeAppliedCodePageFamily;
        context.NativeDecodeHookHitType = seed.NativeDecodeHookHitType;
        context.NativeDecodeObjectCorrelation = 0f;
        context.NativeDecodeClusterCorrelation = Math.Max(0.25f, Math.Min(1f, seed.NativeDecodeClusterCorrelation));
        context.RippleContextText = seed.CurrentText;
        context.RippleSeedCount = Math.Max(1, seedCount);
        context.RippleDistanceRatio = Clamp01(distanceRatio);
        context.RippleSeedQuality = Clamp01(seedQuality);
    }

    public static void ApplyDocumentFamilyEvidence(
        GlyphCoreTextRepairContext context,
        GlyphCoreTextRepairContext seed,
        int seedCount,
        float seedQuality)
    {
        if (context == null || seed == null)
            return;

        context.HasNativeDecodeEvidence = true;
        context.NativeDecodeFamilyMismatch = seed.NativeDecodeFamilyMismatch;
        context.NativeDecodeEvidenceScope = "document-family";
        context.NativeDecodeEvidenceClusterKey = seed.NativeDecodeEvidenceClusterKey;
        context.NativeDecodeSourceCodePageFamily = seed.NativeDecodeSourceCodePageFamily;
        context.NativeDecodeAppliedCodePageFamily = seed.NativeDecodeAppliedCodePageFamily;
        context.NativeDecodeHookHitType = seed.NativeDecodeHookHitType;
        context.NativeDecodeObjectCorrelation = 0f;
        context.NativeDecodeClusterCorrelation = Math.Max(0.25f, Clamp01(seedQuality));
        context.RippleContextText = seed.CurrentText;
        context.RippleSeedCount = Math.Max(1, seedCount);
        context.RippleDistanceRatio = 1f;
        context.RippleSeedQuality = Clamp01(seedQuality);
    }

    public static void Clear()
    {
        lock (Lock)
        {
            ObjectEvidence.Clear();
            ClusterEvidence.Clear();
        }
    }

    private static void ApplyRecord(
        GlyphCoreTextRepairContext context,
        GlyphCoreNativeDecodeEvidenceRecord record,
        string fallbackScope,
        string contextClusterKey)
    {
        context.HasNativeDecodeEvidence = true;
        context.NativeDecodeFamilyMismatch = record.HasMismatch;
        context.NativeDecodeEvidenceScope = string.IsNullOrWhiteSpace(record.Scope) ? fallbackScope : record.Scope;
        context.NativeDecodeEvidenceClusterKey = string.IsNullOrWhiteSpace(record.ClusterKey) ? contextClusterKey : record.ClusterKey;
        context.NativeDecodeSourceCodePageFamily = record.SourceCodePageFamily;
        context.NativeDecodeAppliedCodePageFamily = record.AppliedCodePageFamily;
        context.NativeDecodeHookHitType = record.HookHitType;
        context.NativeDecodeObjectCorrelation = Clamp01(record.ObjectCorrelation);
        context.NativeDecodeClusterCorrelation = Clamp01(record.ClusterCorrelation);
        context.HasHookRawDecodeEvidence = record.HasHookRawDecodeEvidence;
        context.HookRawPayloadSha256 = record.HookRawPayloadSha256;
        context.HookRawPayloadLength = Math.Max(0, record.HookRawPayloadLength);
        context.HookPreferredDecodedText = record.HookPreferredDecodedText;
        context.HookRawCandidateSource = record.HookRawCandidateSource;
        context.HookRawRoundTrip = record.HookRawRoundTrip;
        context.HookRawConfidence = Clamp01(record.HookRawConfidence);
        if (context.NativeDecodeObjectCorrelation <= 0 && record.HasObjectHandle)
            context.NativeDecodeObjectCorrelation = 1f;
        if (context.NativeDecodeClusterCorrelation <= 0 && !string.IsNullOrWhiteSpace(record.ClusterKey))
            context.NativeDecodeClusterCorrelation = 1f;
    }

    private static string BuildObjectKey(string drawingSha256, string drawingPath, string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
            return string.Empty;

        string drawingKey = BuildDrawingKey(drawingSha256, drawingPath);
        return string.IsNullOrEmpty(drawingKey) ? string.Empty : drawingKey + "|object|" + handle;
    }

    private static string BuildEvidenceClusterKey(string drawingSha256, string drawingPath, string clusterKey)
    {
        if (string.IsNullOrWhiteSpace(clusterKey))
            return string.Empty;

        string drawingKey = BuildDrawingKey(drawingSha256, drawingPath);
        return string.IsNullOrEmpty(drawingKey) ? string.Empty : drawingKey + "|cluster|" + clusterKey;
    }

    public static string BuildContextClusterKey(GlyphCoreTextRepairContext context)
    {
        return string.Join("|", new[]
        {
            context.Layer ?? string.Empty,
            context.OwnerBlockName ?? string.Empty,
            context.TextStyleName ?? string.Empty,
            context.TextStyleFileName ?? string.Empty,
            context.TextStyleBigFontFileName ?? string.Empty
        });
    }

    private static string BuildDrawingKey(string drawingSha256, string drawingPath)
    {
        if (!string.IsNullOrWhiteSpace(drawingSha256))
            return drawingSha256;

        return drawingPath ?? string.Empty;
    }

    private static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));

    private static string Sha256Hex(byte[] data)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(data);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
            builder.Append(value.ToString("x2"));
        return builder.ToString();
    }
}

internal sealed class GlyphCoreDbTextDecodeEvidence
{
    public string DrawingSha256 { get; set; } = string.Empty;
    public string DrawingPath { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string ClusterKey { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string SourceCodePageFamily { get; set; } = string.Empty;
    public string AppliedCodePageFamily { get; set; } = string.Empty;
    public string HookHitType { get; set; } = string.Empty;
    public float ObjectCorrelation { get; set; }
    public float ClusterCorrelation { get; set; }
    public string RawPayloadSha256 { get; set; } = string.Empty;
    public int RawPayloadLength { get; set; }
    public string PreferredDecodedText { get; set; } = string.Empty;
    public string CandidateSource { get; set; } = string.Empty;
    public bool RawRoundTrip { get; set; }
    public float RawConfidence { get; set; }

    public bool HasRawPayload =>
        RawPayloadLength > 0 || !string.IsNullOrWhiteSpace(RawPayloadSha256);
}

internal sealed class GlyphCoreNativeDecodeEvidenceRecord
{
    public string DrawingSha256 { get; set; } = string.Empty;
    public string DrawingPath { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string ClusterKey { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string SourceCodePageFamily { get; set; } = string.Empty;
    public string AppliedCodePageFamily { get; set; } = string.Empty;
    public string HookHitType { get; set; } = string.Empty;
    public float ObjectCorrelation { get; set; }
    public float ClusterCorrelation { get; set; }
    public bool HasHookRawDecodeEvidence { get; set; }
    public string HookRawPayloadSha256 { get; set; } = string.Empty;
    public int HookRawPayloadLength { get; set; }
    public string HookPreferredDecodedText { get; set; } = string.Empty;
    public string HookRawCandidateSource { get; set; } = string.Empty;
    public bool HookRawRoundTrip { get; set; }
    public float HookRawConfidence { get; set; }

    public bool HasObjectHandle => !string.IsNullOrWhiteSpace(Handle);

    public bool HasMismatch =>
        !string.IsNullOrWhiteSpace(SourceCodePageFamily)
        && !string.IsNullOrWhiteSpace(AppliedCodePageFamily)
        && !string.Equals(SourceCodePageFamily, AppliedCodePageFamily, StringComparison.OrdinalIgnoreCase);
}
