using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.FontMapping;
using AFR.GlyphCore.TextRepair;

namespace AFR.Services.GlyphCore.TextRepair;

internal static class GlyphCoreNativeDbTextEvidenceProjector
{
    private const string Tag = "DBTextGlyphCoreHook";
    private const int LogLimit = 80;
    private const int MinimumPendingRawEvidenceCount = 16;
    private const float MinimumPendingRawEvidenceRatio = 0.02f;

    private static readonly object PendingRawLock = new();
    private static readonly Dictionary<string, Dictionary<string, PendingRawEquivalentEvidence>> PendingRawEvidenceByDrawing = new(StringComparer.OrdinalIgnoreCase);

#if !NETFRAMEWORK
    private static int _codePageProviderRegistered;
#endif
    private static int _registerCount;
    private static int _provenanceMissCount;
    private static int _textMismatchCount;
    private static int _familyMismatchMissCount;
    private static int _upstreamMismatchEvidenceCount;
    private static int _rawEquivalentEvidenceCount;
    private static int _rawUnsupportedCodePageCount;
    private static int _rawNoBytesCount;
    private static int _rawAppliedDecodeMismatchCount;
    private static int _rawAlternateDecodeMissCount;
    private static int _rawPolicyRejectedCount;
    private static int _rawTextCarrierPendingCount;
    private static int _rawTextCarrierPromotedCount;
    private static int _rawPendingPromotedCount;
    private static int _rawPendingSuppressedCount;
    private static int _errorCount;
    private static int _logCount;

    public static void TryRegister(
        DBText dbText,
        GlyphCoreDrawingIdentity drawing,
        GlyphCoreTextRepairContext context)
    {
        if (dbText == null || context == null)
            return;

        try
        {
            if (!TryGetNativeImpTextPointer(dbText, out IntPtr impText)
                || !DbTextDwgInFieldsScopeHook.TryGetProvenance(impText, out NativeDbTextProvenance provenance))
            {
                Interlocked.Increment(ref _provenanceMissCount);
                return;
            }

            string currentText = context.CurrentText ?? string.Empty;
            if (!MatchesCurrentText(provenance, impText, currentText))
            {
                Interlocked.Increment(ref _textMismatchCount);
                TryLog($"丢弃: handle={context.Handle}, impText=0x{impText.ToInt64():X}, reason=text-mismatch");
                return;
            }

            bool hasUpstream = DbTextUpstreamDecodeProbeHook.TryGetProbeSummary(
                impText,
                out NativeUpstreamDecodeProbeSummary upstream);
            int sourceCodePageId = ResolveSourceCodePageId(provenance, hasUpstream, upstream);
            int appliedCodePageId = ResolveAppliedCodePageId(provenance, hasUpstream, upstream);
            string sourceFamily = CodePageIdToFamily(sourceCodePageId);
            string appliedFamily = CodePageIdToFamily(appliedCodePageId);
            byte[] rawPayload = SelectRawPayload(provenance, hasUpstream, upstream);
            string preferredDecodedText = string.Empty;
            bool rawRoundTrip = false;
            if (string.IsNullOrWhiteSpace(sourceFamily)
                || string.IsNullOrWhiteSpace(appliedFamily)
                || string.Equals(sourceFamily, appliedFamily, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryBuildRawEquivalentEvidence(
                        currentText,
                        sourceCodePageId,
                        hasUpstream,
                        upstream,
                        out sourceFamily,
                        out appliedFamily,
                        out rawPayload,
                        out preferredDecodedText,
                        out rawRoundTrip,
                        out RawEquivalentRejectReason rawRejectReason))
                {
                    Interlocked.Increment(ref _familyMismatchMissCount);
                    IncrementRawReject(rawRejectReason);
                    if (rawRejectReason == RawEquivalentRejectReason.TextCarrierPending)
                    {
                        StorePendingRawEquivalentEvidence(
                            drawing,
                            context,
                            sourceFamily,
                            appliedFamily,
                            rawPayload,
                            preferredDecodedText,
                            rawRoundTrip,
                            rawRejectReason == RawEquivalentRejectReason.TextCarrierPending);
                    }

                    return;
                }

                Interlocked.Increment(ref _rawEquivalentEvidenceCount);
            }

            if (hasUpstream && upstream.CodePageMismatchCount > 0)
                Interlocked.Increment(ref _upstreamMismatchEvidenceCount);

            if (string.IsNullOrWhiteSpace(preferredDecodedText))
            {
                preferredDecodedText = BuildPreferredDecodedText(
                    currentText,
                    sourceCodePageId,
                    sourceFamily,
                    appliedFamily,
                    out rawRoundTrip);
            }
            string clusterKey = GlyphCoreNativeDecodeEvidenceStore.BuildContextClusterKey(context);
            GlyphCoreNativeDecodeEvidenceStore.RegisterDbTextDecodeEvidence(
                drawing.Sha256,
                drawing.Path,
                context.Handle,
                clusterKey,
                sourceFamily,
                appliedFamily,
                rawPayload,
                preferredDecodedText,
                rawRoundTrip,
                rawPayload.Length > 0 || !string.IsNullOrWhiteSpace(preferredDecodedText) ? 1f : 0.85f);

            int count = Interlocked.Increment(ref _registerCount);
            if (count <= LogLimit)
            {
                TryLog(
                    $"register-evidence: handle={context.Handle}, impText=0x{impText.ToInt64():X}, " +
                    $"source={sourceFamily}, applied={appliedFamily}, raw={rawPayload.Length}, upstream={hasUpstream}, " +
                    $"preferred={(string.IsNullOrWhiteSpace(preferredDecodedText) ? 0 : preferredDecodedText.Length)}");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            DiagnosticLogger.LogError(Tag + ": register DBText native evidence failed", ex);
        }
    }

    public static string GetSummary()
        => $"registered={_registerCount}, provenanceMiss={_provenanceMissCount}, textMismatch={_textMismatchCount}, " +
           $"familyNoMismatch={_familyMismatchMissCount}, upstreamMismatch={_upstreamMismatchEvidenceCount}, " +
           $"rawEquivalent={_rawEquivalentEvidenceCount}, rawUnsupportedCp={_rawUnsupportedCodePageCount}, " +
           $"rawNoBytes={_rawNoBytesCount}, rawAppliedMismatch={_rawAppliedDecodeMismatchCount}, " +
           $"rawAltMiss={_rawAlternateDecodeMissCount}, rawPolicyRejected={_rawPolicyRejectedCount}, " +
           $"rawTextCarrierPending={_rawTextCarrierPendingCount}, rawTextCarrierPromoted={_rawTextCarrierPromotedCount}, " +
           $"rawPendingPromoted={_rawPendingPromotedCount}, rawPendingSuppressed={_rawPendingSuppressedCount}, errors={_errorCount}";

    public static int PromotePendingRawEquivalentEvidence(GlyphCoreDrawingIdentity drawing, int scannedCount)
    {
        string drawingKey = BuildDrawingKey(drawing);
        if (string.IsNullOrWhiteSpace(drawingKey))
            return 0;

        PendingRawEquivalentEvidence[] records;
        lock (PendingRawLock)
        {
            if (!PendingRawEvidenceByDrawing.TryGetValue(drawingKey, out Dictionary<string, PendingRawEquivalentEvidence>? byHandle)
                || byHandle.Count == 0)
                return 0;

            records = new PendingRawEquivalentEvidence[byHandle.Count];
            byHandle.Values.CopyTo(records, 0);
            PendingRawEvidenceByDrawing.Remove(drawingKey);
        }

        if (!ShouldPromotePendingRawEquivalentEvidence(records, scannedCount))
        {
            Interlocked.Add(ref _rawPendingSuppressedCount, records.Length);
            TryLog($"pending-raw-suppressed: drawing={drawing.FileName}, count={records.Length}, scanned={scannedCount}");
            return 0;
        }

        for (int i = 0; i < records.Length; i++)
        {
            PendingRawEquivalentEvidence record = records[i];
            GlyphCoreNativeDecodeEvidenceStore.RegisterDbTextDecodeEvidence(
                record.DrawingSha256,
                record.DrawingPath,
                record.Handle,
                record.ClusterKey,
                record.SourceFamily,
                record.AppliedFamily,
                record.RawPayload,
                record.PreferredDecodedText,
                record.RawRoundTrip,
                record.TextCarrierOnly ? 0.72f : 0.75f);
        }

        Interlocked.Add(ref _registerCount, records.Length);
        Interlocked.Add(ref _rawEquivalentEvidenceCount, records.Length);
        Interlocked.Add(ref _rawPendingPromotedCount, records.Length);
        Interlocked.Add(ref _rawTextCarrierPromotedCount, CountTextCarrierOnly(records));
        TryLog($"pending-raw-promoted: drawing={drawing.FileName}, count={records.Length}, scanned={scannedCount}");
        return records.Length;
    }

    public static void Clear()
    {
        Interlocked.Exchange(ref _registerCount, 0);
        Interlocked.Exchange(ref _provenanceMissCount, 0);
        Interlocked.Exchange(ref _textMismatchCount, 0);
        Interlocked.Exchange(ref _familyMismatchMissCount, 0);
        Interlocked.Exchange(ref _upstreamMismatchEvidenceCount, 0);
        Interlocked.Exchange(ref _rawEquivalentEvidenceCount, 0);
        Interlocked.Exchange(ref _rawUnsupportedCodePageCount, 0);
        Interlocked.Exchange(ref _rawNoBytesCount, 0);
        Interlocked.Exchange(ref _rawAppliedDecodeMismatchCount, 0);
        Interlocked.Exchange(ref _rawAlternateDecodeMissCount, 0);
        Interlocked.Exchange(ref _rawPolicyRejectedCount, 0);
        Interlocked.Exchange(ref _rawTextCarrierPendingCount, 0);
        Interlocked.Exchange(ref _rawTextCarrierPromotedCount, 0);
        Interlocked.Exchange(ref _rawPendingPromotedCount, 0);
        Interlocked.Exchange(ref _rawPendingSuppressedCount, 0);
        Interlocked.Exchange(ref _errorCount, 0);
        Interlocked.Exchange(ref _logCount, 0);
        lock (PendingRawLock)
            PendingRawEvidenceByDrawing.Clear();
    }

    private static bool TryGetNativeImpTextPointer(DBText dbText, out IntPtr impText)
    {
        impText = IntPtr.Zero;
        try
        {
            IntPtr nativeObject = dbText.UnmanagedObject;
            if (nativeObject == IntPtr.Zero)
                return false;

            impText = Marshal.ReadIntPtr(nativeObject, IntPtr.Size);
            return impText != IntPtr.Zero;
        }
        catch
        {
            impText = IntPtr.Zero;
            return false;
        }
    }

    private static bool MatchesCurrentText(NativeDbTextProvenance provenance, IntPtr impText, string currentText)
    {
        if (string.IsNullOrEmpty(currentText))
            return false;

        if (!string.IsNullOrEmpty(provenance.NativeText)
            && !string.Equals(provenance.NativeText, currentText, StringComparison.Ordinal))
            return false;

        if (DbTextDwgInFieldsScopeHook.TryReadCurrentNativeText(impText, out string liveNativeText)
            && !string.Equals(liveNativeText, currentText, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static int ResolveSourceCodePageId(
        NativeDbTextProvenance provenance,
        bool hasUpstream,
        NativeUpstreamDecodeProbeSummary upstream)
    {
        if (IsMeaningfulCodePageId(provenance.CodePageId))
            return provenance.CodePageId;

        if (hasUpstream && IsMeaningfulCodePageId(upstream.LastFilerCodePageId))
            return upstream.LastFilerCodePageId;

        return 0;
    }

    private static int ResolveAppliedCodePageId(
        NativeDbTextProvenance provenance,
        bool hasUpstream,
        NativeUpstreamDecodeProbeSummary upstream)
    {
        if (IsMeaningfulCodePageId(provenance.AppliedCodePageId)
            && provenance.AppliedCodePageId != provenance.CodePageId)
            return provenance.AppliedCodePageId;

        if (!hasUpstream)
            return provenance.AppliedCodePageId;

        int sourceCodePageId = ResolveSourceCodePageId(provenance, hasUpstream, upstream);
        if (IsDifferentMeaningfulCodePage(upstream.LastContextCodePageId, sourceCodePageId))
            return upstream.LastContextCodePageId;
        if (IsDifferentMeaningfulCodePage(upstream.LastBridgeCodePageId, sourceCodePageId))
            return upstream.LastBridgeCodePageId;
        if (IsDifferentMeaningfulCodePage(upstream.LastCifCodePageId, sourceCodePageId))
            return upstream.LastCifCodePageId;

        return provenance.AppliedCodePageId;
    }

    private static byte[] SelectRawPayload(
        NativeDbTextProvenance provenance,
        bool hasUpstream,
        NativeUpstreamDecodeProbeSummary upstream)
    {
        if (hasUpstream)
        {
            if (upstream.LastDTextFullInputBytes.Length > 0)
                return upstream.LastDTextFullInputBytes;
            if (upstream.LastCifInputBytes.Length > 0)
                return upstream.LastCifInputBytes;
            if (upstream.CursorDeltaStreamBytes.Length > 0)
                return upstream.CursorDeltaStreamBytes;
            if (upstream.LastInputBytes.Length > 0)
                return upstream.LastInputBytes;
        }

        if (provenance.DwgInRaw.RawBytes.Length > 0)
            return provenance.DwgInRaw.RawBytes;

        return provenance.NativeDbcsBytes.Length > 0 ? provenance.NativeDbcsBytes : [];
    }

    private static string BuildPreferredDecodedText(
        string currentText,
        int sourceCodePageId,
        string sourceFamily,
        string appliedFamily,
        out bool rawRoundTrip)
    {
        rawRoundTrip = false;
        if (TextEditorDbcsDecodeHook.TryDecodeWithObservedEvidence(
                currentText,
                sourceCodePageId,
                out string decoded,
                out _))
        {
            rawRoundTrip = true;
            return decoded;
        }

        if (TryConvertCarrier(currentText, appliedFamily, sourceFamily, out decoded, out rawRoundTrip))
            return decoded;

        return string.Empty;
    }

    private static bool TryBuildRawEquivalentEvidence(
        string currentText,
        int appliedCodePageId,
        bool hasUpstream,
        NativeUpstreamDecodeProbeSummary upstream,
        out string sourceFamily,
        out string appliedFamily,
        out byte[] rawPayload,
        out string preferredDecodedText,
        out bool rawRoundTrip,
        out RawEquivalentRejectReason rejectReason)
    {
        sourceFamily = string.Empty;
        appliedFamily = string.Empty;
        rawPayload = [];
        preferredDecodedText = string.Empty;
        rawRoundTrip = false;
        rejectReason = RawEquivalentRejectReason.None;

        if (string.IsNullOrWhiteSpace(currentText)
            || !TryMapAutoCadCodePageIdToWindowsCodePage(appliedCodePageId, out int appliedWindowsCodePage)
            || !TryGetAlternateDbcsWindowsCodePage(appliedWindowsCodePage, out int alternateWindowsCodePage))
        {
            rejectReason = RawEquivalentRejectReason.UnsupportedCodePage;
            return false;
        }

        byte[] textBytes = SelectUpstreamTextBytes(hasUpstream, upstream);
        if (textBytes.Length == 0)
        {
            if (TryBuildCurrentTextCarrierEquivalentEvidence(
                    currentText,
                    appliedWindowsCodePage,
                    alternateWindowsCodePage,
                    out sourceFamily,
                    out appliedFamily,
                    out preferredDecodedText,
                    out rawRoundTrip))
            {
                rejectReason = RawEquivalentRejectReason.TextCarrierPending;
            }
            else
            {
                rejectReason = RawEquivalentRejectReason.NoBytes;
            }

            return false;
        }

        if (!TryDecodeBytes(textBytes, appliedWindowsCodePage, out string decodedWithApplied)
            || !string.Equals(decodedWithApplied, currentText, StringComparison.Ordinal))
        {
            rejectReason = RawEquivalentRejectReason.AppliedDecodeMismatch;
            return false;
        }

        if (!TryDecodeBytes(textBytes, alternateWindowsCodePage, out string alternateDecoded)
            || string.Equals(alternateDecoded, currentText, StringComparison.Ordinal))
        {
            rejectReason = RawEquivalentRejectReason.AlternateDecodeMissingOrSame;
            return false;
        }

        sourceFamily = WindowsCodePageToFamily(alternateWindowsCodePage);
        appliedFamily = WindowsCodePageToFamily(appliedWindowsCodePage);
        rawPayload = textBytes;
        preferredDecodedText = alternateDecoded;
        rawRoundTrip = true;

        if (!TryAcceptSimplifiedChineseEngineeringPolicyCandidate(currentText, alternateDecoded))
        {
            rejectReason = RawEquivalentRejectReason.PolicyRejected;
            return false;
        }

        return !string.IsNullOrWhiteSpace(sourceFamily)
               && !string.IsNullOrWhiteSpace(appliedFamily)
               && !string.Equals(sourceFamily, appliedFamily, StringComparison.OrdinalIgnoreCase);
    }

    private static void StorePendingRawEquivalentEvidence(
        GlyphCoreDrawingIdentity drawing,
        GlyphCoreTextRepairContext context,
        string sourceFamily,
        string appliedFamily,
        byte[] rawPayload,
        string preferredDecodedText,
        bool rawRoundTrip,
        bool textCarrierOnly = false)
    {
        if (string.IsNullOrWhiteSpace(context.Handle)
            || string.IsNullOrWhiteSpace(sourceFamily)
            || string.IsNullOrWhiteSpace(appliedFamily)
            || string.Equals(sourceFamily, appliedFamily, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(preferredDecodedText))
            return;

        string drawingKey = BuildDrawingKey(drawing);
        if (string.IsNullOrWhiteSpace(drawingKey))
            return;

        var record = new PendingRawEquivalentEvidence(
            drawing.Sha256,
            drawing.Path,
            context.Handle,
            GlyphCoreNativeDecodeEvidenceStore.BuildContextClusterKey(context),
            sourceFamily,
            appliedFamily,
            rawPayload,
            preferredDecodedText,
            rawRoundTrip,
            textCarrierOnly);

        lock (PendingRawLock)
        {
            if (!PendingRawEvidenceByDrawing.TryGetValue(drawingKey, out Dictionary<string, PendingRawEquivalentEvidence>? byHandle))
            {
                byHandle = new Dictionary<string, PendingRawEquivalentEvidence>(StringComparer.OrdinalIgnoreCase);
                PendingRawEvidenceByDrawing[drawingKey] = byHandle;
            }

            byHandle[context.Handle] = record;
        }
    }

    private static bool ShouldPromotePendingRawEquivalentEvidence(PendingRawEquivalentEvidence[] records, int scannedCount)
    {
        int pendingCount = records.Length;
        if (pendingCount == scannedCount && scannedCount > 0 && AllTextCarrierOnly(records))
            return true;

        if (pendingCount < MinimumPendingRawEvidenceCount)
            return false;

        if (scannedCount <= 0)
            return true;

        return pendingCount / (float)Math.Max(1, scannedCount) >= MinimumPendingRawEvidenceRatio;
    }

    private static bool AllTextCarrierOnly(PendingRawEquivalentEvidence[] records)
    {
        if (records.Length == 0)
            return false;

        for (int i = 0; i < records.Length; i++)
        {
            if (!records[i].TextCarrierOnly)
                return false;
        }

        return true;
    }

    private static string BuildDrawingKey(GlyphCoreDrawingIdentity drawing)
        => !string.IsNullOrWhiteSpace(drawing.Sha256)
            ? drawing.Sha256
            : (drawing.Path ?? string.Empty);

    private static void IncrementRawReject(RawEquivalentRejectReason reason)
    {
        switch (reason)
        {
            case RawEquivalentRejectReason.UnsupportedCodePage:
                Interlocked.Increment(ref _rawUnsupportedCodePageCount);
                break;
            case RawEquivalentRejectReason.NoBytes:
                Interlocked.Increment(ref _rawNoBytesCount);
                break;
            case RawEquivalentRejectReason.AppliedDecodeMismatch:
                Interlocked.Increment(ref _rawAppliedDecodeMismatchCount);
                break;
            case RawEquivalentRejectReason.AlternateDecodeMissingOrSame:
                Interlocked.Increment(ref _rawAlternateDecodeMissCount);
                break;
            case RawEquivalentRejectReason.PolicyRejected:
                Interlocked.Increment(ref _rawPolicyRejectedCount);
                break;
            case RawEquivalentRejectReason.TextCarrierPending:
                Interlocked.Increment(ref _rawTextCarrierPendingCount);
                break;
        }
    }

    private static byte[] SelectUpstreamTextBytes(bool hasUpstream, NativeUpstreamDecodeProbeSummary upstream)
    {
        if (!hasUpstream)
            return [];

        byte[] bytes = TrimTrailingNullBytes(upstream.LastDTextFullInputBytes);
        if (bytes.Length > 0)
            return bytes;

        bytes = TrimTrailingNullBytes(upstream.LastCifInputBytes);
        return bytes.Length > 0 ? bytes : [];
    }

    private static byte[] TrimTrailingNullBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return [];

        int length = bytes.Length;
        while (length > 0 && bytes[length - 1] == 0)
            length--;

        if (length == bytes.Length)
            return bytes;
        if (length == 0)
            return [];

        var trimmed = new byte[length];
        Array.Copy(bytes, trimmed, length);
        return trimmed;
    }

    private static bool TryDecodeBytes(byte[] bytes, int windowsCodePage, out string decoded)
    {
        decoded = string.Empty;
        if (bytes.Length == 0)
            return false;

        try
        {
            EnsureCodePages();
            Encoding encoding = GetStrictEncoding(windowsCodePage);
            decoded = encoding.GetString(bytes);
            return !string.IsNullOrEmpty(decoded);
        }
        catch
        {
            decoded = string.Empty;
            return false;
        }
    }

    private static bool TryAcceptSimplifiedChineseEngineeringPolicyCandidate(string currentText, string candidate)
    {
        if (string.IsNullOrEmpty(currentText)
            || string.IsNullOrEmpty(candidate)
            || string.Equals(currentText, candidate, StringComparison.Ordinal))
            return false;

        if (!string.Equals(ExtractAsciiSkeleton(currentText), ExtractAsciiSkeleton(candidate), StringComparison.Ordinal))
            return false;

        AnalyzeSimplifiedChineseEngineeringText(
            currentText,
            out int currentDisallowed,
            out _,
            out _);
        if (currentDisallowed == 0)
            return false;

        AnalyzeSimplifiedChineseEngineeringText(
            candidate,
            out int candidateDisallowed,
            out int candidateSimplifiedCjk,
            out int candidatePrivateUse);
        return candidateDisallowed == 0
               && candidatePrivateUse == 0
               && candidateSimplifiedCjk > 0;
    }

    private static bool TryBuildCurrentTextCarrierEquivalentEvidence(
        string currentText,
        int appliedWindowsCodePage,
        int alternateWindowsCodePage,
        out string sourceFamily,
        out string appliedFamily,
        out string preferredDecodedText,
        out bool rawRoundTrip)
    {
        sourceFamily = WindowsCodePageToFamily(alternateWindowsCodePage);
        appliedFamily = WindowsCodePageToFamily(appliedWindowsCodePage);
        preferredDecodedText = string.Empty;
        rawRoundTrip = false;

        if (string.IsNullOrWhiteSpace(sourceFamily)
            || string.IsNullOrWhiteSpace(appliedFamily)
            || string.Equals(sourceFamily, appliedFamily, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryConvertCarrier(currentText, sourceFamily, appliedFamily, out string candidate, out rawRoundTrip)
            || !rawRoundTrip)
            return false;

        if (!TryAcceptSimplifiedChineseEngineeringPolicyCandidate(currentText, candidate)
            || !HasStrongCarrierArtifact(currentText, candidate))
            return false;

        preferredDecodedText = candidate;
        return true;
    }

    private static bool HasStrongCarrierArtifact(string currentText, string candidate)
    {
        AnalyzeSimplifiedChineseEngineeringText(
            currentText,
            out int currentDisallowed,
            out _,
            out int currentPrivateUse);
        AnalyzeSimplifiedChineseEngineeringText(
            candidate,
            out _,
            out int candidateSimplifiedCjk,
            out int candidatePrivateUse);

        if (currentPrivateUse > 0 && candidatePrivateUse == 0)
            return true;

        if (ContainsDbcsCarrierPunctuation(currentText))
            return true;

        int currentCjk = CountCjkUnifiedIdeographs(currentText);
        return currentCjk >= 3
               && currentDisallowed >= Math.Max(2, (int)Math.Ceiling(currentCjk * 0.6))
               && candidateSimplifiedCjk >= Math.Max(2, (int)Math.Ceiling(currentCjk * 0.5));
    }

    private static bool ContainsDbcsCarrierPunctuation(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if ((ch >= '\u3100' && ch <= '\u312F')
                || (ch >= '\uFE50' && ch <= '\uFE6F'))
                return true;
        }

        return false;
    }

    private static int CountCjkUnifiedIdeographs(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsCjkUnifiedIdeograph(text[i]))
                count++;
        }

        return count;
    }

    private static string ExtractAsciiSkeleton(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x7F && !char.IsWhiteSpace(ch))
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static void AnalyzeSimplifiedChineseEngineeringText(
        string text,
        out int disallowedCount,
        out int simplifiedCjkCount,
        out int privateUseCount)
    {
        disallowedCount = 0;
        simplifiedCjkCount = 0;
        privateUseCount = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x7F)
                continue;

            if (ch >= '\uE000' && ch <= '\uF8FF')
            {
                privateUseCount++;
                disallowedCount++;
                continue;
            }

            if (ch == '\uFFFD' || char.IsSurrogate(ch) || (char.IsControl(ch) && ch != '\t'))
            {
                disallowedCount++;
                continue;
            }

            if (IsCjkUnifiedIdeograph(ch))
            {
                if (IsCommonSimplifiedChineseChar(ch))
                    simplifiedCjkCount++;
                else
                    disallowedCount++;
            }
        }
    }

    private static bool IsCjkUnifiedIdeograph(char ch)
        => ch >= '\u4E00' && ch <= '\u9FFF';

    private static bool IsCommonSimplifiedChineseChar(char ch)
    {
        try
        {
            EnsureCodePages();
            Encoding gb2312 = Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            byte[] bytes = gb2312.GetBytes(new[] { ch });
            return bytes.Length == 2 && bytes[0] >= 0xB0 && bytes[0] <= 0xF7;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertCarrier(
        string text,
        string carrierFamily,
        string targetFamily,
        out string decoded,
        out bool roundTrip)
    {
        decoded = string.Empty;
        roundTrip = false;
        if (!TryFamilyToCodePage(carrierFamily, out int carrierCodePage)
            || !TryFamilyToCodePage(targetFamily, out int targetCodePage))
            return false;

        try
        {
            EnsureCodePages();
            Encoding carrier = GetStrictEncoding(carrierCodePage);
            Encoding target = GetStrictEncoding(targetCodePage);
            byte[] carrierBytes = carrier.GetBytes(text);
            string candidate = target.GetString(carrierBytes);
            if (string.IsNullOrEmpty(candidate) || string.Equals(candidate, text, StringComparison.Ordinal))
                return false;

            byte[] candidateBytes = target.GetBytes(candidate);
            roundTrip = string.Equals(carrier.GetString(candidateBytes), text, StringComparison.Ordinal);
            decoded = candidate;
            return true;
        }
        catch
        {
            decoded = string.Empty;
            roundTrip = false;
            return false;
        }
    }

    private static bool TryFamilyToCodePage(string family, out int codePage)
    {
        if (family.IndexOf("big5", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            codePage = 950;
            return true;
        }

        if (family.IndexOf("gbk", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            codePage = 936;
            return true;
        }

        if (family.IndexOf("utf8", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            codePage = Encoding.UTF8.CodePage;
            return true;
        }

        codePage = 0;
        return false;
    }

    private static string CodePageIdToFamily(int codePageId)
        => codePageId switch
        {
            0x27 or 936 => "gbk",
            0x28 or 950 => "big5",
            65001 => "utf8",
            _ => string.Empty
        };

    private static string WindowsCodePageToFamily(int windowsCodePage)
        => windowsCodePage switch
        {
            936 => "gbk",
            950 => "big5",
            65001 => "utf8",
            _ => string.Empty
        };

    private static bool TryMapAutoCadCodePageIdToWindowsCodePage(int codePageId, out int windowsCodePage)
    {
        windowsCodePage = codePageId switch
        {
            0x27 or 936 => 936,
            0x28 or 950 => 950,
            65001 => 65001,
            _ => 0
        };
        return windowsCodePage != 0;
    }

    private static bool TryGetAlternateDbcsWindowsCodePage(int windowsCodePage, out int alternateWindowsCodePage)
    {
        alternateWindowsCodePage = windowsCodePage switch
        {
            936 => 950,
            950 => 936,
            _ => 0
        };
        return alternateWindowsCodePage != 0;
    }

    private static bool IsMeaningfulCodePageId(int codePageId)
        => codePageId is 0x27 or 0x28 or 936 or 950 or 65001;

    private static bool IsDifferentMeaningfulCodePage(int codePageId, int sourceCodePageId)
        => IsMeaningfulCodePageId(codePageId)
           && IsMeaningfulCodePageId(sourceCodePageId)
           && codePageId != sourceCodePageId;

    private static Encoding GetStrictEncoding(int codePage)
    {
        if (codePage == Encoding.UTF8.CodePage)
            return new UTF8Encoding(false, throwOnInvalidBytes: true);

        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static void EnsureCodePages()
    {
#if !NETFRAMEWORK
        if (Interlocked.Exchange(ref _codePageProviderRegistered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
    }

    private static void TryLog(string message)
    {
        if (Interlocked.Increment(ref _logCount) <= LogLimit)
            DiagnosticLogger.Log(Tag, message);
    }

    private static int CountTextCarrierOnly(PendingRawEquivalentEvidence[] records)
    {
        int count = 0;
        for (int i = 0; i < records.Length; i++)
        {
            if (records[i].TextCarrierOnly)
                count++;
        }

        return count;
    }

    private enum RawEquivalentRejectReason
    {
        None,
        UnsupportedCodePage,
        NoBytes,
        AppliedDecodeMismatch,
        AlternateDecodeMissingOrSame,
        PolicyRejected,
        TextCarrierPending
    }

    private readonly record struct PendingRawEquivalentEvidence(
        string DrawingSha256,
        string DrawingPath,
        string Handle,
        string ClusterKey,
        string SourceFamily,
        string AppliedFamily,
        byte[] RawPayload,
        string PreferredDecodedText,
        bool RawRoundTrip,
        bool TextCarrierOnly);
}
