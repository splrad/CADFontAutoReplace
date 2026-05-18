using System;
using System.Collections.Generic;

namespace AFR.GlyphCore.TextRepair;

internal sealed class GlyphCoreTextRepairProblemDetection
{
    private GlyphCoreTextRepairProblemDetection(bool hasProblem, string reason, IReadOnlyList<GlyphCoreTextRepairCandidate> candidates)
    {
        HasProblem = hasProblem;
        Reason = reason;
        Candidates = candidates;
    }

    public bool HasProblem { get; }
    public string Reason { get; }
    public IReadOnlyList<GlyphCoreTextRepairCandidate> Candidates { get; }

    public static GlyphCoreTextRepairProblemDetection NoProblem(IReadOnlyList<GlyphCoreTextRepairCandidate> candidates) =>
        new(false, "no-suspicious-dbtext", candidates);

    public static GlyphCoreTextRepairProblemDetection Problem(string reason, IReadOnlyList<GlyphCoreTextRepairCandidate> candidates) =>
        new(true, reason, candidates);
}

internal static class GlyphCoreTextRepairProblemDetector
{
    public static GlyphCoreTextRepairProblemDetection Detect(GlyphCoreTextRepairContext context)
    {
        string current = context.CurrentText ?? string.Empty;
        IReadOnlyList<GlyphCoreTextRepairCandidate> candidates = GlyphCoreTextRepairCandidateGenerator.BuildCandidates(current, context);
        if (string.IsNullOrWhiteSpace(current))
            return GlyphCoreTextRepairProblemDetection.NoProblem(candidates);

        if (!HasStrongNativeDecodeEvidence(context))
            return GlyphCoreTextRepairProblemDetection.NoProblem(candidates);

        return GlyphCoreTextRepairProblemDetection.Problem(BuildNativeDecodeReason(context), candidates);
    }

    private static bool HasStrongNativeDecodeEvidence(GlyphCoreTextRepairContext context)
    {
        if (!context.HasNativeDecodeEvidence || !context.NativeDecodeFamilyMismatch)
            return false;

        if (!IsScope(context, "ripple")
            && !IsScope(context, "document-family")
            && !IsHookHitType(context, "dbtext"))
            return false;

        if (context.NativeDecodeObjectCorrelation > 0)
            return true;

        return context.NativeDecodeClusterCorrelation > 0
               && (IsScope(context, "cluster")
                   || IsScope(context, "ripple")
                   || IsScope(context, "document-family"));
    }

    private static string BuildNativeDecodeReason(GlyphCoreTextRepairContext context)
    {
        string scope = string.IsNullOrWhiteSpace(context.NativeDecodeEvidenceScope)
            ? "native-decode"
            : context.NativeDecodeEvidenceScope;
        string source = string.IsNullOrWhiteSpace(context.NativeDecodeSourceCodePageFamily)
            ? "unknown"
            : context.NativeDecodeSourceCodePageFamily;
        string applied = string.IsNullOrWhiteSpace(context.NativeDecodeAppliedCodePageFamily)
            ? "unknown"
            : context.NativeDecodeAppliedCodePageFamily;
        return $"native-dbcs-codepage-mismatch:{scope}:{source}-as-{applied}";
    }

    private static bool IsScope(GlyphCoreTextRepairContext context, string scope)
        => string.Equals(context.NativeDecodeEvidenceScope, scope, StringComparison.OrdinalIgnoreCase);

    private static bool IsHookHitType(GlyphCoreTextRepairContext context, string hookHitType)
        => (context.NativeDecodeHookHitType ?? string.Empty).IndexOf(hookHitType, StringComparison.OrdinalIgnoreCase) >= 0;
}

