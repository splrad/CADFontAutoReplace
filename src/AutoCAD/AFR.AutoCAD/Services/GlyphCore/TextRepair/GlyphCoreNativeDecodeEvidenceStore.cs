using System;
using System.Collections.Generic;
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
        int seedCount)
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

    public bool HasObjectHandle => !string.IsNullOrWhiteSpace(Handle);

    public bool HasMismatch =>
        !string.IsNullOrWhiteSpace(SourceCodePageFamily)
        && !string.IsNullOrWhiteSpace(AppliedCodePageFamily)
        && !string.Equals(SourceCodePageFamily, AppliedCodePageFamily, StringComparison.OrdinalIgnoreCase);
}
