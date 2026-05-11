#if DEBUG
using System.Reflection;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AFR.FontMapping;
using AFR.Services;

[assembly: CommandClass(typeof(AFR.DebugCommands.TextEntityInspectCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// DEBUG-only text entity inspector for DBCS investigation.
/// </summary>
public sealed class TextEntityInspectCommand
{
    private static readonly bool EnableNativeImpTextIndependentSourceProbe = false;
    private const int ObjectSourceScanSampleLimit = 80;
    private const int ObjectSourceScanMetadataPerObjectLimit = 4;
    private const int ObjectSourceScanLinkedStringPerObjectLimit = 6;

    [CommandMethod("AFRINSPECTTEXT", CommandFlags.Modal)]
    public static void Execute()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc?.Editor;
        if (doc == null || ed == null)
            return;

        var options = new PromptNestedEntityOptions("\n选择仍然乱码的文字对象: ");
        PromptNestedEntityResult result = ed.GetNestedEntity(options);
        if (result.Status != PromptStatus.OK)
            return;

        DiagnosticLogger.BeginDocument(doc.Name, "", "", "");
        DiagnosticLogger.BeginPhase("检查文字对象");

        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var obj = tr.GetObject(result.ObjectId, OpenMode.ForRead, false, true);
            string report = BuildReport(obj, tr, SafeGetContainers(result), Safe(() => doc.Database.Filename));
            ed.WriteMessage("\n" + report + "\n");
            DiagnosticLogger.Log("TextInspect", report);
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n检查失败: {ex.Message}\n");
            DiagnosticLogger.LogError("TextInspect 执行失败", ex);
        }
        finally
        {
            DiagnosticLogger.EndPhase();
            DiagnosticLogger.WriteSummary();
        }
    }

    [CommandMethod("AFRSCANTEXTSOURCES", CommandFlags.Modal)]
    public static void ScanTextSources()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc?.Editor;
        if (doc == null || ed == null)
            return;

        DiagnosticLogger.BeginDocument(doc.Name, "", "", "");
        DiagnosticLogger.BeginPhase("扫描 DBText 对象绑定冗余源");

        try
        {
            string report = BuildObjectBoundSourceScanReport(doc.Database);
            ed.WriteMessage("\n" + report + "\n");
            DiagnosticLogger.Log("TextSourceScan", report);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n扫描失败: {ex.Message}\n");
            DiagnosticLogger.LogError("TextSourceScan 执行失败", ex);
        }
        finally
        {
            DiagnosticLogger.EndPhase();
            DiagnosticLogger.WriteSummary();
        }
    }

    private static string BuildObjectBoundSourceScanReport(Database db)
    {
        int scannedBlocks = 0;
        int skippedBlocks = 0;
        int scannedDbText = 0;
        int withMetadata = 0;
        int withCandidateTextMetadata = 0;
        int metadataStringTotal = 0;
        int structuralMetadataStringTotal = 0;
        int candidateTextMetadataStringTotal = 0;
        int unavailableMetadataDiagnostics = 0;
        int metadataContainsCurrent = 0;
        int metadataDiffersFromCurrent = 0;
        int metadataContainsObservedPreview = 0;
        int observedPreviewAvailable = 0;
        int withLinkedMetadataReferences = 0;
        int linkedMetadataReferenceTotal = 0;
        int resolvedLinkedMetadataObjectTotal = 0;
        int linkedObjectStringTotal = 0;
        int linkedObjectStringEqualsCurrent = 0;
        int linkedObjectStringDiffersFromCurrent = 0;
        int linkedObjectStringEqualsObservedPreview = 0;
        int linkedObservedPreviewAvailable = 0;
        int errors = 0;
        var samples = new List<string>();

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in blockTable)
        {
            BlockTableRecord block;
            try
            {
                block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                if (block.IsFromExternalReference || block.IsDependent)
                {
                    skippedBlocks++;
                    continue;
                }

                scannedBlocks++;
            }
            catch
            {
                errors++;
                continue;
            }

            foreach (ObjectId entityId in block)
            {
                DBText? dbText;
                try
                {
                    dbText = tr.GetObject(entityId, OpenMode.ForRead, false, true) as DBText;
                }
                catch
                {
                    errors++;
                    continue;
                }

                if (dbText == null)
                    continue;

                scannedDbText++;
                string currentText = dbText.TextString ?? string.Empty;
                List<MetadataObjectReference> linkedReferences = CollectObjectBoundMetadataReferences(dbText, tr);
                if (linkedReferences.Count > 0)
                {
                    withLinkedMetadataReferences++;
                    linkedMetadataReferenceTotal += linkedReferences.Count;

                    List<string> linkedStrings = CollectLinkedObjectStrings(
                        dbText,
                        tr,
                        linkedReferences,
                        out int resolvedLinkedObjects,
                        out int linkedErrors);
                    resolvedLinkedMetadataObjectTotal += resolvedLinkedObjects;
                    errors += linkedErrors;
                    linkedObjectStringTotal += linkedStrings.Count;

                    if (linkedStrings.Count > 0)
                    {
                        bool linkedContainsCurrent = linkedStrings.Any(value =>
                            string.Equals(ExtractMetadataPayload(value), currentText, StringComparison.Ordinal));
                        if (linkedContainsCurrent)
                            linkedObjectStringEqualsCurrent++;

                        bool linkedDiffersFromCurrent = linkedStrings.Any(value =>
                        {
                            string payload = ExtractMetadataPayload(value);
                            return !string.IsNullOrEmpty(payload)
                                && !string.Equals(payload, currentText, StringComparison.Ordinal);
                        });
                        if (linkedDiffersFromCurrent)
                            linkedObjectStringDiffersFromCurrent++;

                        if (TextEditorDbcsDecodeHook.TryPreviewWithLastObservedEvidence(
                                currentText,
                                out string linkedObservedPreview,
                                out _))
                        {
                            linkedObservedPreviewAvailable++;
                            if (linkedStrings.Any(value =>
                                    string.Equals(ExtractMetadataPayload(value), linkedObservedPreview, StringComparison.Ordinal)))
                            {
                                linkedObjectStringEqualsObservedPreview++;
                            }
                        }

                        if (samples.Count < ObjectSourceScanSampleLimit)
                        {
                            samples.Add(
                                $"Handle={dbText.Handle}, Layer='{Escape(dbText.Layer)}', " +
                                $"Text='{Escape(TrimForReport(currentText))}', " +
                                $"LinkedReferences={linkedReferences.Count}, LinkedStrings={linkedStrings.Count}");
                            int emitted = Math.Min(linkedStrings.Count, ObjectSourceScanLinkedStringPerObjectLimit);
                            for (int i = 0; i < emitted; i++)
                                samples.Add($"  LinkedObjectString[{i}]={Escape(TrimForReport(linkedStrings[i]))}");
                        }
                    }
                }

                List<string> rawValues = CollectObjectBoundMetadataStrings(dbText, tr);
                List<string> values = rawValues
                    .Where(value => !IsMetadataUnavailableDiagnostic(value))
                    .ToList();
                unavailableMetadataDiagnostics += rawValues.Count - values.Count;
                if (values.Count == 0)
                    continue;

                withMetadata++;
                metadataStringTotal += values.Count;
                List<string> candidateValues = values
                    .Where(value => !IsKnownStructuralMetadataString(value))
                    .ToList();
                structuralMetadataStringTotal += values.Count - candidateValues.Count;
                candidateTextMetadataStringTotal += candidateValues.Count;
                if (candidateValues.Count == 0)
                    continue;

                withCandidateTextMetadata++;

                bool containsCurrent = candidateValues.Any(value =>
                    string.Equals(ExtractMetadataPayload(value), currentText, StringComparison.Ordinal));
                if (containsCurrent)
                    metadataContainsCurrent++;

                bool differsFromCurrent = candidateValues.Any(value =>
                {
                    string payload = ExtractMetadataPayload(value);
                    return !string.IsNullOrEmpty(payload)
                        && !string.Equals(payload, currentText, StringComparison.Ordinal);
                });
                if (differsFromCurrent)
                    metadataDiffersFromCurrent++;

                if (TextEditorDbcsDecodeHook.TryPreviewWithLastObservedEvidence(
                        currentText,
                        out string observedPreview,
                        out _))
                {
                    observedPreviewAvailable++;
                    if (candidateValues.Any(value =>
                            string.Equals(ExtractMetadataPayload(value), observedPreview, StringComparison.Ordinal)))
                    {
                        metadataContainsObservedPreview++;
                    }
                }

                if (samples.Count < ObjectSourceScanSampleLimit)
                {
                    samples.Add(
                        $"Handle={dbText.Handle}, Layer='{Escape(dbText.Layer)}', " +
                        $"Text='{Escape(TrimForReport(currentText))}', " +
                        $"MetadataCount={values.Count}, CandidateTextMetadataCount={candidateValues.Count}");
                    int emitted = Math.Min(candidateValues.Count, ObjectSourceScanMetadataPerObjectLimit);
                    for (int i = 0; i < emitted; i++)
                        samples.Add($"  CandidateTextMetadata[{i}]={Escape(TrimForReport(candidateValues[i]))}");
                }
            }
        }

        tr.Commit();

        var lines = new List<string>
        {
            "=== AFR DBText Object-Bound Source Scan ===",
            "Scope: DBText direct XData + ExtensionDictionary/Xrecord strings, plus referenced objects resolved from the same metadata",
            "Decision: read-only diagnostic; no automatic writeback",
            $"ScannedBlocks: {scannedBlocks}",
            $"SkippedBlocks: {skippedBlocks}",
            $"ScannedDbText: {scannedDbText}",
            $"DbTextWithObjectBoundMetadata: {withMetadata}",
            $"ObjectBoundMetadataStringTotal: {metadataStringTotal}",
            $"StructuralMetadataStringTotal: {structuralMetadataStringTotal}",
            $"DbTextWithCandidateTextMetadata: {withCandidateTextMetadata}",
            $"CandidateTextMetadataStringTotal: {candidateTextMetadataStringTotal}",
            $"UnavailableMetadataDiagnostics: {unavailableMetadataDiagnostics}",
            $"ObservedPreviewAvailable: {observedPreviewAvailable}",
            $"CandidateMetadataEqualsCurrentText: {metadataContainsCurrent}",
            $"CandidateMetadataDiffersFromCurrentText: {metadataDiffersFromCurrent}",
            $"CandidateMetadataEqualsObservedPreview: {metadataContainsObservedPreview}",
            "ObservedPreviewDecision: diagnostic-only derived from current Unicode; equality here is not sufficient for automatic repair",
            $"DbTextWithLinkedMetadataReferences: {withLinkedMetadataReferences}",
            $"LinkedMetadataReferenceTotal: {linkedMetadataReferenceTotal}",
            $"ResolvedLinkedMetadataObjectTotal: {resolvedLinkedMetadataObjectTotal}",
            $"LinkedObjectStringTotal: {linkedObjectStringTotal}",
            $"LinkedObservedPreviewAvailable: {linkedObservedPreviewAvailable}",
            $"LinkedObjectStringEqualsCurrentText: {linkedObjectStringEqualsCurrent}",
            $"LinkedObjectStringDiffersFromCurrentText: {linkedObjectStringDiffersFromCurrent}",
            $"LinkedObjectStringEqualsObservedPreview: {linkedObjectStringEqualsObservedPreview}",
            "LinkedObjectDecision: read-only diagnostic; referenced object strings are not automatic repair evidence until their static meaning is proven",
            linkedObjectStringTotal == 0
                ? "LinkedConclusion: no-linked-object-string-source-found-in-full-dbtext-scan"
                : "LinkedConclusion: linked-object-strings-present-review-static-meaning-before-any-repair",
            candidateTextMetadataStringTotal == 0
                ? "Conclusion: no-candidate-object-bound-text-source-found-in-full-dbtext-scan"
                : "Conclusion: candidate-object-bound-text-strings-present-review-samples-and-static-meaning-before-any-repair",
            $"Errors: {errors}",
            "Samples:"
        };

        if (samples.Count == 0)
        {
            lines.Add("  <none>");
        }
        else
        {
            foreach (string sample in samples)
                lines.Add("  " + sample);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildReport(DBObject obj, Transaction tr, ObjectId[] containers, string drawingFilePath)
    {
        var lines = new List<string>
        {
            "=== AFR Text Entity Inspect ===",
            $"ObjectId: {obj.ObjectId}",
            $"Handle: {Safe(() => obj.Handle.ToString())}",
            $".NET Type: {obj.GetType().FullName}",
            $"RXClass: {Safe(() => obj.GetRXClass()?.Name ?? "<null>")}",
            $"DxfName: {Safe(() => obj.GetRXClass()?.DxfName ?? "<null>")}"
        };

        if (obj is Entity entity)
        {
            lines.Add($"Layer: {Safe(() => entity.Layer)}");
            lines.Add($"ColorIndex: {Safe(() => entity.ColorIndex.ToString())}");
        }

        AppendContainers(lines, containers, tr);
        AppendKnownText(lines, obj, tr, drawingFilePath);
        AppendObjectBoundMetadataStrings(lines, obj, tr);
        AppendStringProperties(lines, obj);
        return string.Join(Environment.NewLine, lines);
    }

    private static ObjectId[] SafeGetContainers(PromptNestedEntityResult result)
    {
        try { return result.GetContainers() ?? Array.Empty<ObjectId>(); }
        catch { return Array.Empty<ObjectId>(); }
    }

    private static void AppendContainers(List<string> lines, ObjectId[] containers, Transaction tr)
    {
        lines.Add($"NestedContainerCount: {containers.Length}");
        for (int i = 0; i < containers.Length; i++)
        {
            ObjectId id = containers[i];
            lines.Add($"Container[{i}].ObjectId: {id}");
            lines.Add($"Container[{i}].Handle: {Safe(() => tr.GetObject(id, OpenMode.ForRead, false, true).Handle.ToString())}");
            lines.Add($"Container[{i}].Type: {Safe(() => tr.GetObject(id, OpenMode.ForRead, false, true).GetType().FullName ?? "<null>")}");
            lines.Add($"Container[{i}].RXClass: {Safe(() => tr.GetObject(id, OpenMode.ForRead, false, true).GetRXClass()?.Name ?? "<null>")}");
        }
    }

    private static void AppendKnownText(List<string> lines, DBObject obj, Transaction tr, string drawingFilePath)
    {
        switch (obj)
        {
            case DBText dbText:
                lines.Add($"DBText.TextString: {Escape(dbText.TextString)}");
                AppendObservedDecodePreview(lines, dbText.TextString);
                AppendNativeDbTextEvidence(lines, dbText, drawingFilePath);
                lines.Add($"TextStyleId: {Safe(() => DescribeTextStyle(dbText.TextStyleId, tr))}");
                AppendCjkShxDisplayBoundary(lines, dbText.TextString ?? string.Empty, dbText.TextStyleId, tr);
                break;
            case MText mText:
                lines.Add($"MText.Contents: {Escape(mText.Contents)}");
                lines.Add($"MText.Text: {Escape(mText.Text)}");
                AppendObservedDecodePreview(lines, mText.Text);
                lines.Add($"TextStyleId: {Safe(() => DescribeTextStyle(mText.TextStyleId, tr))}");
                break;
            case Dimension dimension:
                lines.Add($"Dimension.DimensionText: {Escape(dimension.DimensionText)}");
                lines.Add($"Dimension.DimensionStyle: {Safe(() => dimension.DimensionStyle.ToString())}");
                break;
            case MLeader mLeader:
                lines.Add($"MLeader.ContentType: {mLeader.ContentType}");
                lines.Add($"MLeader.MText?.Contents: {Escape(mLeader.MText?.Contents ?? string.Empty)}");
                lines.Add($"MLeader.TextStyleId: {Safe(() => DescribeTextStyle(mLeader.TextStyleId, tr))}");
                break;
            case Table table:
                lines.Add($"Table.Rows: {table.Rows.Count}, Columns: {table.Columns.Count}");
                AppendTablePreview(lines, table);
                break;
        }
    }

    private static void AppendObservedDecodePreview(List<string> lines, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (TextEditorDbcsDecodeHook.TryPreviewWithLastObservedEvidence(text, out string decoded, out string reason))
        {
            lines.Add($"ObservedDecodePreview: {Escape(decoded)}");
            lines.Add($"ObservedDecodeCoverage: full");
        }
        else
        {
            lines.Add($"ObservedDecodeCoverage: {reason}");
        }
    }

    private static void AppendNativeDbTextEvidence(List<string> lines, DBText dbText, string drawingFilePath)
    {
        string text = dbText.TextString ?? string.Empty;
        if (!DbTextEncodingRepairService.TryGetNativeImpTextPointer(dbText, out IntPtr impText))
        {
            lines.Add("NativeImpText: <unavailable>");
            lines.Add("NativeProvenance: missing");
            return;
        }

        lines.Add($"NativeImpText: 0x{impText.ToInt64():X}");
        if (!DbTextDwgInFieldsScopeHook.TryGetProvenance(impText, out NativeDbTextProvenance provenance))
        {
            AppendNativeLiveTextEvidence(lines, impText, text, null);
            lines.Add("NativeProvenance: missing");
            return;
        }

        bool textMatches = string.Equals(provenance.NativeText, text, StringComparison.Ordinal);
        lines.Add("NativeProvenance: available");
        lines.Add($"NativeProvenanceCodePage: {DwgFilerCodePageScopeHook.FormatCodePageId(provenance.CodePageId)}");
        lines.Add($"NativeProvenanceFiler: 0x{provenance.Filer.ToInt64():X}");
        lines.Add($"NativeDbcsDecodedChars: {provenance.NativeDbcsDecodedText.Length}");
        if (!string.IsNullOrEmpty(provenance.NativeDbcsDecodedText))
            lines.Add($"NativeDbcsDecodedText: {Escape(provenance.NativeDbcsDecodedText)}");
        if (provenance.NativeDbcsBytes.Length > 0)
            lines.Add($"NativeDbcsRawBytes: {FormatBytes(provenance.NativeDbcsBytes, 48)}");
        AppendNativeLiveTextEvidence(lines, impText, text, provenance.NativeText);
        AppendNativeFilerVirtualSnapshot(lines, provenance.FilerVirtualSnapshot);
        AppendNativeReadStringEvents(lines, provenance.ReadStringEvents);
        AppendNativeTextSetSourceEvents(lines, provenance.TextSetSourceEvents, text, provenance.NativeText);
        (byte[] upstreamCursorCandidateBytes, byte[] upstreamDTextFullInputBytes) =
            AppendNativeUpstreamDecodeProbe(lines, impText, text);
        AppendNativeDwgInRawSnapshot(
            lines,
            provenance.DwgInRaw,
            provenance.CodePageId,
            text,
            provenance.NativeText,
            upstreamCursorCandidateBytes,
            upstreamDTextFullInputBytes);
        lines.Add($"NativeProvenanceTextMatchesCurrent: {textMatches}");
        if (!textMatches)
        {
            lines.Add($"NativeProvenanceText: {Escape(provenance.NativeText)}");
            lines.Add("NativeRepairDecision: reject native-text-mismatch");
            return;
        }

        bool nativeExactBlocksObserved = false;
        if (DbTextEncodingRepairService.TryBuildTextFromNativeDbcsBytes(
                text,
                provenance.NativeDbcsBytes,
                provenance.CodePageId,
                out string rawDecoded,
                out string rawReason))
        {
            lines.Add($"NativeRawDecodePreview: {Escape(rawDecoded)}");
            lines.Add($"NativeRawDecodeCoverage: {rawReason}");
            string rawCandidate = rawDecoded;
            if (DbTextEncodingRepairService.TryNormalizePrivateUseSymbolsFromOriginal(
                    text,
                    rawDecoded,
                    out string normalizedRaw,
                    out string rawNormalizeReason))
            {
                rawCandidate = normalizedRaw;
                lines.Add($"NativeRawPrivateUseNormalizedPreview: {Escape(normalizedRaw)}");
                lines.Add($"NativeRawPrivateUseNormalizedCoverage: {rawNormalizeReason}");
            }

            if (string.Equals(text, rawCandidate, StringComparison.Ordinal))
            {
                lines.Add("NativeRawRepairDecision: reject already-exact-native-text");
            }
            else if (DbTextEncodingRepairService.TryValidateNativeDecodedTextForRepair(
                         rawCandidate,
                         out string rawValidReason))
            {
                lines.Add("NativeRawRepairDecision: diagnostic-only no-auto-write");
            }
            else
            {
                lines.Add($"NativeRawRepairDecision: reject {rawValidReason}");
            }
        }
        else
        {
            lines.Add($"NativeRawDecodeCoverage: {rawReason}");
            lines.Add("NativeRawRepairDecision: reject no-full-native-dbcs-bytes");
        }

        if (DbTextEncodingRepairService.TryBuildTextFromNativeDbcsSequence(
                text,
                provenance.NativeDbcsDecodedText,
                out string decoded,
                out string reason))
        {
            lines.Add($"NativeExactDecodePreview: {Escape(decoded)}");
            lines.Add($"NativeExactDecodeCoverage: {reason}");
            if (string.Equals(text, decoded, StringComparison.Ordinal))
            {
                nativeExactBlocksObserved = true;
                lines.Add("NativeExactRepairDecision: reject already-exact-native-text");
            }
            else if (DbTextEncodingRepairService.TryValidateNativeDecodedTextForRepair(
                         decoded,
                         out string validReason,
                         allowPrivateUse: true))
            {
                lines.Add("NativeExactRepairDecision: allow");
                return;
            }
            else
            {
                lines.Add($"NativeExactRepairDecision: reject {validReason}");
                return;
            }
        }
        else
        {
            lines.Add($"NativeExactDecodeCoverage: {reason}");
            lines.Add("NativeExactRepairDecision: reject no-full-native-dbcs-sequence");
        }

        AppendObservedCarrierEvidence(
            lines,
            text,
            provenance.CodePageId,
            provenance.DwgInRaw,
            provenance.NativeDbcsBytes,
            upstreamCursorCandidateBytes,
            upstreamDTextFullInputBytes,
            drawingFilePath);
        AppendNativeImpTextIndependentSourceProbe(lines, impText, text, provenance.CodePageId);

        if (TextEditorDbcsDecodeHook.TryDecodeWithObservedEvidence(
                text,
                provenance.CodePageId,
                out string observedDecoded,
                out string observedReason,
                allowNativeExpansion: true))
        {
            lines.Add($"ObservedObjectCodePageDecodePreview: {Escape(observedDecoded)}");
            lines.Add($"ObservedObjectCodePageDecodeCoverage: {observedReason}");
            lines.Add($"ObservedFallbackWrongPathCharsCaptured: {provenance.NativeDbcsDecodedText.Length}");
            string repairCandidate = observedDecoded;
            if (DbTextEncodingRepairService.TryNormalizePrivateUseSymbolsFromOriginal(
                    text,
                    observedDecoded,
                    out string normalizedObserved,
                    out string normalizeReason))
            {
                repairCandidate = normalizedObserved;
                lines.Add($"ObservedFallbackPrivateUseNormalizedPreview: {Escape(normalizedObserved)}");
                lines.Add($"ObservedFallbackPrivateUseNormalizedCoverage: {normalizeReason}");
            }

            if (string.Equals(text, repairCandidate, StringComparison.Ordinal))
            {
                lines.Add("ObservedFallbackRepairDecision: reject already-exact-native-text");
            }
            else if (DbTextEncodingRepairService.TryValidateNativeDecodedTextForRepair(
                         repairCandidate,
                         out string observedValidReason))
            {
                lines.Add(nativeExactBlocksObserved
                    ? "ObservedFallbackRepairDecision: diagnostic-only blocked-by-native-exact-current"
                    : "ObservedFallbackRepairDecision: diagnostic-only no-auto-write-without-complete-native-bytes");
            }
            else
            {
                lines.Add($"ObservedFallbackRepairDecision: reject {observedValidReason}");
            }
        }
    }

    private static void AppendObservedCarrierEvidence(
        List<string> lines,
        string currentText,
        int codePageId,
        NativeDwgInRawSnapshot snapshot,
        byte[] nativeDbcsBytes,
        byte[] upstreamCursorCandidateBytes,
        byte[] upstreamDTextFullInputBytes,
        string drawingFilePath)
    {
        if (!TextEditorDbcsDecodeHook.TryBuildObservedCarrierBytes(
                currentText,
                out byte[] carrierBytes,
                out string carrierReason))
        {
            lines.Add($"ObservedCarrierCoverage: {carrierReason}");
            if (carrierBytes.Length > 0)
                lines.Add($"ObservedCarrierBytes: {FormatBytes(carrierBytes, 96)}");
            lines.Add("ObservedCarrierDecision: diagnostic-only derived-from-current-unicode, no-auto-write");
            return;
        }

        lines.Add($"ObservedCarrierCoverage: {carrierReason}");
        lines.Add($"ObservedCarrierBytes: {FormatBytes(carrierBytes, 96)}");
        lines.Add($"ObservedCarrierLength: {carrierBytes.Length}");

        if (DbTextEncodingRepairService.TryDecodeDwgTextBytesWithCodePage(
                carrierBytes,
                codePageId,
                out string carrierDecoded,
                out int windowsCodePage,
                out string decodeReason))
        {
            lines.Add(
                $"ObservedCarrierDecodeViaObjectCodePage: CP{windowsCodePage} " +
                $"({DwgFilerCodePageScopeHook.FormatCodePageId(codePageId)}) => {Escape(carrierDecoded)}");
        }
        else
        {
            lines.Add($"ObservedCarrierDecodeViaObjectCodePage: unavailable {decodeReason}");
        }

        lines.Add(nativeDbcsBytes.Length > 0
            ? $"ObservedCarrierMatchesNativeDbcsRawBytes: {carrierBytes.SequenceEqual(nativeDbcsBytes)}"
            : "ObservedCarrierMatchesNativeDbcsRawBytes: <none>");
        lines.Add(upstreamCursorCandidateBytes.Length > 0
            ? $"ObservedCarrierMatchesNativeUpstreamCursorDeltaCandidateBytes: {carrierBytes.SequenceEqual(upstreamCursorCandidateBytes)}"
            : "ObservedCarrierMatchesNativeUpstreamCursorDeltaCandidateBytes: <none>");
        lines.Add(upstreamDTextFullInputBytes.Length > 0
            ? $"ObservedCarrierMatchesNativeUpstreamDTextFullInputBytes: {carrierBytes.SequenceEqual(upstreamDTextFullInputBytes)}"
            : "ObservedCarrierMatchesNativeUpstreamDTextFullInputBytes: <none>");
        int rawIndex = IndexOfSequence(snapshot.RawBytes, carrierBytes);
        lines.Add(snapshot.RawBytes.Length > 0
            ? $"ObservedCarrierIndexInNativeDwgInRawBytes: {rawIndex}"
            : "ObservedCarrierIndexInNativeDwgInRawBytes: <none>");
        if (snapshot.IsTruncated)
            lines.Add("ObservedCarrierNativeDwgInRawSearch: diagnostic-only raw-snapshot-truncated");

        byte[]? currentDwgBytes = TryReadFileBytes(drawingFilePath, out string fileBytesReason);
        int snapshotFileIndex = -1;
        if (currentDwgBytes == null)
        {
            lines.Add($"ObservedCarrierIndexInCurrentDwgFileBytes: <unavailable {fileBytesReason}>");
            lines.Add("ObservedCarrierCurrentDwgFileSearch: diagnostic-only file-unavailable");
            lines.Add($"NativeDwgInRawIndexInCurrentDwgFileBytes: <unavailable {fileBytesReason}>");
            lines.Add("ObservedCarrierIndexInCurrentDwgObjectWindow: <unavailable file-unavailable>");
        }
        else
        {
            lines.Add($"ObservedCarrierIndexInCurrentDwgFileBytes: {IndexOfSequence(currentDwgBytes, carrierBytes)}");
            lines.Add($"ObservedCarrierCurrentDwgFileSearch: {FormatGlobalFileSearchEvidence(currentDwgBytes, carrierBytes)}");
            snapshotFileIndex = snapshot.RawBytes.Length == 0 || snapshot.IsTruncated
                ? -1
                : IndexOfSequence(currentDwgBytes, snapshot.RawBytes);
            lines.Add(snapshot.RawBytes.Length == 0
                ? "NativeDwgInRawIndexInCurrentDwgFileBytes: <none>"
                : $"NativeDwgInRawIndexInCurrentDwgFileBytes: {snapshotFileIndex}");
            lines.Add($"NativeDwgInRawCurrentDwgFileSearch: {FormatSnapshotFileSearchEvidence(currentDwgBytes, snapshot)}");
            lines.Add($"ObservedCarrierIndexInCurrentDwgObjectWindow: {IndexOfObjectWindow(currentDwgBytes, snapshotFileIndex, snapshot.RawBytes.Length, carrierBytes)}");
            lines.Add($"ObservedCarrierCurrentDwgObjectWindowSearch: {FormatObjectWindowSearchEvidence(snapshotFileIndex, snapshot.RawBytes.Length, carrierBytes.Length)}");
        }

        if (DbTextEncodingRepairService.TryExtractTerminalDwgTextField(
                snapshot,
                out byte[] terminalBytes,
                out int terminalLengthMarkerOffset,
                out _,
                out _))
        {
            lines.Add($"ObservedCarrierMatchesNativeDwgTerminalBytes: {carrierBytes.SequenceEqual(terminalBytes)}");
            lines.Add($"NativeDwgTerminalLengthForCurrentDwgSearch: {terminalBytes.Length}");
            if (currentDwgBytes == null)
            {
                lines.Add($"NativeDwgTerminalIndexInCurrentDwgFileBytes: <unavailable {fileBytesReason}>");
                lines.Add("NativeDwgTerminalCurrentDwgFileSearch: diagnostic-only file-unavailable");
            }
            else
            {
                int terminalFileIndex = IndexOfSequence(currentDwgBytes, terminalBytes);
                lines.Add($"NativeDwgTerminalIndexInCurrentDwgFileBytes: {terminalFileIndex}");
                lines.Add($"NativeDwgTerminalCurrentDwgFileSearch: {FormatGlobalFileSearchEvidence(currentDwgBytes, terminalBytes)}");
                int expectedTerminalFileIndex = snapshotFileIndex >= 0
                    ? snapshotFileIndex + terminalLengthMarkerOffset + 1
                    : -1;
                lines.Add(expectedTerminalFileIndex >= 0
                    ? $"NativeDwgTerminalExpectedIndexFromObjectWindow: {expectedTerminalFileIndex}"
                    : "NativeDwgTerminalExpectedIndexFromObjectWindow: <unavailable no-object-window>");
                lines.Add(expectedTerminalFileIndex >= 0
                    ? $"NativeDwgTerminalIndexMatchesObjectWindow: {terminalFileIndex == expectedTerminalFileIndex}"
                    : "NativeDwgTerminalIndexMatchesObjectWindow: <unavailable no-object-window>");
            }
        }
        else
        {
            lines.Add("ObservedCarrierMatchesNativeDwgTerminalBytes: <none>");
            lines.Add("NativeDwgTerminalIndexInCurrentDwgFileBytes: <none>");
            lines.Add("NativeDwgTerminalCurrentDwgFileSearch: diagnostic-only no-terminal-field");
        }

        lines.Add("ObservedCarrierDecision: diagnostic-only derived-from-current-unicode, no-auto-write");
    }

    private static void AppendNativeImpTextIndependentSourceProbe(
        List<string> lines,
        IntPtr impText,
        string currentText,
        int codePageId)
    {
        if (!EnableNativeImpTextIndependentSourceProbe)
        {
            lines.Add("NativeImpTextIndependentSourceProbe: disabled-pending-static-offset-verification");
            lines.Add("NativeImpTextIndependentSourceDecision: diagnostic-disabled to avoid unsafe native pointer scan");
            return;
        }

        if (!DbTextEncodingRepairService.TryGetObservedFallbackDiagnosticCandidate(
                currentText,
                codePageId,
                out string candidateText,
                out string candidateReason))
        {
            lines.Add($"NativeImpTextIndependentSourceProbe: not-run no-observed-candidate {candidateReason}");
            lines.Add("NativeImpTextIndependentSourceDecision: diagnostic-only no-auto-write");
            return;
        }

        TextEditorDbcsDecodeHook.TryBuildObservedCarrierBytes(
            currentText,
            out byte[] carrierBytes,
            out _);

        NativeImpTextIndependentSourceReport report =
            NativeImpTextIndependentSourceProbe.Inspect(impText, currentText, candidateText, carrierBytes);

        lines.Add("NativeImpTextIndependentSourceProbe: available");
        lines.Add($"NativeImpTextIndependentCandidate: {Escape(candidateText)}");
        lines.Add($"NativeImpTextIndependentCandidateCoverage: {candidateReason}");
        lines.Add($"NativeImpTextIndependentScannedBytes: {report.ScannedObjectBytes}");
        lines.Add($"NativeImpTextIndependentCurrentInlineWideOffset: {FormatIndex(report.CurrentInlineWideOffset)}");
        lines.Add($"NativeImpTextIndependentCandidateInlineWideOffset: {FormatIndex(report.CandidateInlineWideOffset)}");
        lines.Add(
            "NativeImpTextIndependentWideSlots: " +
            $"wideLike={report.WideLikeSlotCount}, current={report.CurrentWideSlotCount}, " +
            $"candidate={report.CandidateWideSlotCount}, independentCandidate={report.IndependentCandidateWideSlotCount}");
        lines.Add($"NativeImpTextIndependentCarrierByteSlots: {report.CandidateCarrierByteSlotCount}");

        int wideCount = Math.Min(report.WideSlots.Count, 8);
        for (int i = 0; i < wideCount; i++)
        {
            NativeImpTextWideStringSlot slot = report.WideSlots[i];
            lines.Add(
                $"NativeImpTextWideSlot[{i}]: offset=+0x{slot.Offset:X}, ptr=0x{slot.Pointer.ToInt64():X}, " +
                $"len={slot.Length}{(slot.IsTruncated ? "+" : "")}, currentTextPointer={slot.IsCurrentTextPointerSlot}, " +
                $"matchesCurrent={slot.MatchesCurrent}, matchesCandidate={slot.MatchesCandidate}, text='{Escape(slot.Text)}'");
        }

        int byteCount = Math.Min(report.ByteSlots.Count, 8);
        for (int i = 0; i < byteCount; i++)
        {
            NativeImpTextByteSlot slot = report.ByteSlots[i];
            lines.Add(
                $"NativeImpTextByteSlot[{i}]: offset=+0x{slot.Offset:X}, ptr=0x{slot.Pointer.ToInt64():X}, " +
                $"scanned={slot.ScannedBytes}, carrierIndex={slot.CandidateCarrierIndex}, " +
                $"carrierLength={slot.CandidateCarrierLength}, currentTextPointer={slot.IsCurrentTextPointerSlot}");
        }

        lines.Add($"NativeImpTextIndependentSourceDecision: {report.Decision}, no-auto-write-without-verified-source-offset");
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        if (haystack.Length == 0 || needle.Length == 0 || needle.Length > haystack.Length)
            return -1;

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool matched = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return i;
        }

        return -1;
    }

    private static string FormatGlobalFileSearchEvidence(byte[] haystack, byte[] needle)
    {
        int matchCount = CountSequenceOccurrences(haystack, needle, 3);
        string countText = matchCount >= 3 ? ">=3" : matchCount.ToString();
        string shortText = needle.Length < 8 ? ", short-sequence-ambiguous" : "";
        return $"diagnostic-only global-file-search-not-object-scoped, length={needle.Length}, matches={countText}{shortText}";
    }

    private static string FormatSnapshotFileSearchEvidence(byte[] haystack, NativeDwgInRawSnapshot snapshot)
    {
        if (snapshot.RawBytes.Length == 0)
            return "diagnostic-only no-raw-snapshot";
        if (snapshot.IsTruncated)
            return "diagnostic-only raw-snapshot-truncated";

        int matchCount = CountSequenceOccurrences(haystack, snapshot.RawBytes, 3);
        string countText = matchCount >= 3 ? ">=3" : matchCount.ToString();
        return $"diagnostic-only object-raw-window-search, length={snapshot.RawBytes.Length}, matches={countText}";
    }

    private static int IndexOfObjectWindow(byte[] fileBytes, int snapshotFileIndex, int snapshotLength, byte[] needle)
    {
        if (snapshotFileIndex < 0 || snapshotLength <= 0 || needle.Length == 0)
            return -1;

        const int NeighborhoodBytes = 256;
        int start = Math.Max(0, snapshotFileIndex - NeighborhoodBytes);
        int endExclusive = Math.Min(fileBytes.Length, snapshotFileIndex + snapshotLength + NeighborhoodBytes);
        return IndexOfSequence(fileBytes, needle, start, endExclusive);
    }

    private static string FormatObjectWindowSearchEvidence(int snapshotFileIndex, int snapshotLength, int needleLength)
    {
        if (snapshotFileIndex < 0 || snapshotLength <= 0)
            return "diagnostic-only no-object-file-window";

        const int NeighborhoodBytes = 256;
        string shortText = needleLength < 8 ? ", short-sequence-ambiguous" : "";
        return
            "diagnostic-only object-scoped-window-plus-neighborhood, " +
            $"rawStart={snapshotFileIndex}, rawLength={snapshotLength}, neighborhood={NeighborhoodBytes}{shortText}";
    }

    private static string FormatIndex(int index)
    {
        return index < 0 ? "-1" : $"+0x{index:X}";
    }

    private static int CountSequenceOccurrences(byte[] haystack, byte[] needle, int limit)
    {
        if (haystack.Length == 0 || needle.Length == 0 || needle.Length > haystack.Length || limit <= 0)
            return 0;

        int count = 0;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool matched = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            count++;
            if (count >= limit)
                return count;
        }

        return count;
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle, int start, int endExclusive)
    {
        if (haystack.Length == 0 || needle.Length == 0 || needle.Length > haystack.Length)
            return -1;

        int safeStart = Math.Max(0, start);
        int safeEndExclusive = Math.Min(haystack.Length, endExclusive);
        if (safeStart >= safeEndExclusive || needle.Length > safeEndExclusive - safeStart)
            return -1;

        for (int i = safeStart; i <= safeEndExclusive - needle.Length; i++)
        {
            bool matched = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return i;
        }

        return -1;
    }

    private static byte[]? TryReadFileBytes(string drawingFilePath, out string reason)
    {
        reason = "<empty>";
        if (string.IsNullOrWhiteSpace(drawingFilePath))
        {
            reason = "empty-path";
            return null;
        }

        try
        {
            if (!System.IO.File.Exists(drawingFilePath))
            {
                reason = "file-not-found";
                return null;
            }

            using var stream = new System.IO.FileStream(
                drawingFilePath,
                System.IO.FileMode.Open,
                System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            if (stream.Length > int.MaxValue)
            {
                reason = "file-too-large";
                return null;
            }

            var bytes = new byte[(int)stream.Length];
            int totalRead = 0;
            while (totalRead < bytes.Length)
            {
                int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                if (read == 0)
                    break;

                totalRead += read;
            }

            if (totalRead != bytes.Length)
            {
                Array.Resize(ref bytes, totalRead);
                reason = "partial-read";
                return bytes;
            }

            reason = "ok";
            return bytes;
        }
        catch (System.Exception ex)
        {
            reason = ex.GetType().Name;
            return null;
        }
    }

    private static void AppendNativeLiveTextEvidence(
        List<string> lines,
        IntPtr impText,
        string currentText,
        string? provenanceText)
    {
        if (!DbTextDwgInFieldsScopeHook.TryReadCurrentNativeText(impText, out string nativeLiveText))
        {
            lines.Add("NativeLiveText: <unavailable>");
            lines.Add("NativeLiveTextDecision: diagnostic-only native getter unavailable");
            return;
        }

        lines.Add($"NativeLiveText: {Escape(nativeLiveText)}");
        lines.Add($"NativeLiveTextMatchesCurrent: {string.Equals(nativeLiveText, currentText, StringComparison.Ordinal)}");
        if (provenanceText != null)
            lines.Add($"NativeLiveTextMatchesProvenance: {string.Equals(nativeLiveText, provenanceText, StringComparison.Ordinal)}");
        lines.Add("NativeLiveTextDecision: diagnostic-only live native getter, no-auto-write");
    }

    private static (byte[] CursorCandidateBytes, byte[] DTextFullInputBytes) AppendNativeUpstreamDecodeProbe(
        List<string> lines,
        IntPtr impText,
        string currentText)
    {
        if (!DbTextUpstreamDecodeProbeHook.TryGetProbeSummary(impText, out NativeUpstreamDecodeProbeSummary summary))
        {
            lines.Add("NativeUpstreamDecodeProbe: missing");
            return ([], []);
        }

        byte[] cursorCandidateBytes = BuildCursorDeltaCandidateBytes(
            summary.CursorDeltaStreamBytes,
            summary.CursorDeltaPendingBytes);

        lines.Add("NativeUpstreamDecodeProbe: available");
        lines.Add($"NativeUpstreamDispatcherHits: {summary.DispatcherHitCount}");
        lines.Add($"NativeUpstreamCodePageMismatches: {summary.CodePageMismatchCount}");
        lines.Add($"NativeUpstreamLastApi: {summary.LastApiName}");
        lines.Add($"NativeUpstreamLastReturnRva: 0x{summary.LastReturnRva:X}");
        lines.Add($"NativeUpstreamLastFilerCodePage: {DwgFilerCodePageScopeHook.FormatCodePageId(summary.LastFilerCodePageId)}");
        lines.Add($"NativeUpstreamLastContextCodePage: {DwgFilerCodePageScopeHook.FormatCodePageId(summary.LastContextCodePageId)}");
        lines.Add($"NativeUpstreamLastInputBytes: {FormatBytes(summary.LastInputBytes, 24)}");
        lines.Add($"NativeUpstreamCifToWideHits: {summary.CifToWideCharHitCount}");
        lines.Add($"NativeUpstreamCifLastReturnRva: 0x{summary.LastCifReturnRva:X}");
        lines.Add($"NativeUpstreamCifLastFilerCodePage: {DwgFilerCodePageScopeHook.FormatCodePageId(summary.LastCifFilerCodePageId)}");
        lines.Add($"NativeUpstreamCifLastArgCodePage: {DwgFilerCodePageScopeHook.FormatCodePageId(summary.LastCifCodePageId)}");
        lines.Add($"NativeUpstreamCifLastMode: 0x{summary.LastCifMode:X}");
        lines.Add($"NativeUpstreamCifLastSourceLength: {summary.LastCifInputLength}");
        lines.Add($"NativeUpstreamCifLastReturnValue: {summary.LastCifReturnValue}");
        lines.Add(
            $"NativeUpstreamCifLastInputBytes: {FormatBytes(summary.LastCifInputBytes, 160)}" +
            (summary.LastCifInputTruncated ? " ..." : string.Empty));
        if (!string.IsNullOrEmpty(summary.LastCifOutputText))
        {
            lines.Add(
                $"NativeUpstreamCifLastOutputText: {Escape(summary.LastCifOutputText)}" +
                (summary.LastCifOutputTruncated ? "..." : string.Empty));
        }
        lines.Add("NativeUpstreamCifDecision: diagnostic-only complete function-argument bytes before CIF decode, no-auto-write");
        lines.Add($"NativeUpstreamUtf16ToWideHits: {summary.Utf16ToWideGetWideBufferHitCount}");
        lines.Add($"NativeUpstreamUtf16ToWideLastReturnRva: 0x{summary.LastUtf16ToWideReturnRva:X}");
        lines.Add($"NativeUpstreamUtf16ToWideLastFilerCodePage: {DwgFilerCodePageScopeHook.FormatCodePageId(summary.LastUtf16ToWideFilerCodePageId)}");
        lines.Add($"NativeUpstreamUtf16ToWideLastCharCount: {summary.LastUtf16ToWideCharCount}");
        lines.Add($"NativeUpstreamUtf16ToWideLastReturnValue: 0x{summary.LastUtf16ToWideReturnValue:X2}");
        lines.Add($"NativeUpstreamUtf16ToWideSourceEqualsOutput: {summary.LastUtf16ToWideSourceEqualsOutput}");
        lines.Add(
            $"NativeUpstreamUtf16ToWideLastInputBytes: {FormatBytes(summary.LastUtf16ToWideInputBytes, 192)}" +
            (summary.LastUtf16ToWideInputTruncated ? " ..." : string.Empty));
        if (summary.LastUtf16ToWideInputBytes.Length > 0)
        {
            byte[] currentUtf16Bytes = System.Text.Encoding.Unicode.GetBytes(currentText);
            lines.Add($"NativeUpstreamUtf16ToWideMatchesCurrentUtf16: {summary.LastUtf16ToWideInputBytes.SequenceEqual(currentUtf16Bytes)}");
            if (summary.LastUtf16ToWideInputBytes.Length % 2 == 0)
            {
                string utf16Text = System.Text.Encoding.Unicode.GetString(summary.LastUtf16ToWideInputBytes);
                lines.Add($"NativeUpstreamUtf16ToWideDecodedAsUtf16: {Escape(utf16Text)}");
            }
        }
        if (!string.IsNullOrEmpty(summary.LastUtf16ToWideOutputText))
        {
            lines.Add(
                $"NativeUpstreamUtf16ToWideLastOutputText: {Escape(summary.LastUtf16ToWideOutputText)}" +
                (summary.LastUtf16ToWideOutputTruncated ? "..." : string.Empty));
            lines.Add($"NativeUpstreamUtf16ToWideOutputMatchesCurrent: {string.Equals(summary.LastUtf16ToWideOutputText, currentText, StringComparison.Ordinal)}");
        }
        lines.Add("NativeUpstreamUtf16ToWideDecision: diagnostic-only complete Utf16ToWide helper source buffer, no-auto-write");
        lines.Add($"NativeUpstreamDTextFullInputHits: {summary.DTextFullInputHitCount}");
        lines.Add($"NativeUpstreamDTextFullInputHookRva: 0x{summary.LastDTextFullInputHookRva:X}");
        lines.Add($"NativeUpstreamDTextFullInputFilerCodePage: {DwgFilerCodePageScopeHook.FormatCodePageId(summary.LastDTextFullInputFilerCodePageId)}");
        lines.Add(
            $"NativeUpstreamDTextFullInputBytes: {FormatBytes(summary.LastDTextFullInputBytes, 192)}" +
            (summary.LastDTextFullInputTruncated ? " ..." : string.Empty));
        if (summary.LastDTextFullInputBytes.Length > 0)
        {
            lines.Add($"NativeUpstreamDTextFullInputMatchesCursorDeltaCandidate: {summary.LastDTextFullInputBytes.SequenceEqual(cursorCandidateBytes)}");
            byte[] currentEncodedByDTextCodePage = [];
            bool matchesCurrentEncoded = false;
            bool currentEncodedAvailable = DbTextEncodingRepairService.TryEncodeDwgTextWithCodePage(
                    currentText,
                    summary.LastDTextFullInputFilerCodePageId,
                    out currentEncodedByDTextCodePage,
                    out int dtextEncodeCodePage,
                    out string dtextEncodeReason);
            if (currentEncodedAvailable)
            {
                matchesCurrentEncoded = summary.LastDTextFullInputBytes.SequenceEqual(currentEncodedByDTextCodePage);
                lines.Add(
                    $"NativeUpstreamDTextFullInputMatchesCurrentEncodedByHookCodePage: " +
                    $"{matchesCurrentEncoded} (CP{dtextEncodeCodePage})");
            }
            else
            {
                lines.Add($"NativeUpstreamDTextFullInputMatchesCurrentEncodedByHookCodePage: <unavailable {dtextEncodeReason}>");
            }

            bool matchesCurrentAcadEscaped = false;
            if (TryBuildAcadUnicodeEscapedBytes(
                    currentText,
                    summary.LastDTextFullInputFilerCodePageId,
                    out byte[] currentAcadEscapedBytes,
                    out int currentAcadEscapedCodePage,
                    out string currentAcadEscapedReason))
            {
                matchesCurrentAcadEscaped = summary.LastDTextFullInputBytes.SequenceEqual(currentAcadEscapedBytes);
                lines.Add(
                    $"NativeUpstreamDTextFullInputMatchesCurrentAcadEscapedByHookCodePage: " +
                    $"{matchesCurrentAcadEscaped} (CP{currentAcadEscapedCodePage})");
                if (!currentEncodedAvailable)
                    lines.Add($"CurrentTextAcadEscapedByHookCodePage: {FormatBytes(currentAcadEscapedBytes, 192)}");
            }
            else
            {
                lines.Add($"NativeUpstreamDTextFullInputMatchesCurrentAcadEscapedByHookCodePage: <unavailable {currentAcadEscapedReason}>");
            }

            bool matchesObservedCarrier = false;
            if (TextEditorDbcsDecodeHook.TryBuildObservedCarrierBytes(
                    currentText,
                    out byte[] observedCarrierBytes,
                    out string observedCarrierReason))
            {
                matchesObservedCarrier = summary.LastDTextFullInputBytes.SequenceEqual(observedCarrierBytes);
                lines.Add($"NativePreDecodeDTextInputMatchesObservedCarrier: {matchesObservedCarrier}");
            }
            else
            {
                lines.Add($"NativePreDecodeDTextInputMatchesObservedCarrier: <unavailable {observedCarrierReason}>");
            }

            bool decodedRepresentsCurrent = false;
            if (DbTextEncodingRepairService.TryDecodeDwgTextBytesWithCodePage(
                    summary.LastDTextFullInputBytes,
                    summary.LastDTextFullInputFilerCodePageId,
                    out string dtextDecoded,
                    out int dtextDecodeCodePage,
                    out string dtextDecodeReason))
            {
                lines.Add(
                    $"NativeUpstreamDTextFullInputDecode: CP{dtextDecodeCodePage} " +
                    $"({DwgFilerCodePageScopeHook.FormatCodePageId(summary.LastDTextFullInputFilerCodePageId)}) => {Escape(dtextDecoded)}");
                bool dtextDecodedMatchesCurrent = string.Equals(dtextDecoded, currentText, StringComparison.Ordinal);
                decodedRepresentsCurrent = dtextDecodedMatchesCurrent;
                lines.Add($"NativeUpstreamDTextFullInputMatchesCurrent: {dtextDecodedMatchesCurrent}");
                if (TryExpandAcadUnicodeEscapes(dtextDecoded, out string expandedDTextDecoded, out int dtextExpandedCount))
                {
                    bool expandedMatchesCurrent = string.Equals(expandedDTextDecoded, currentText, StringComparison.Ordinal);
                    decodedRepresentsCurrent |= expandedMatchesCurrent;
                    lines.Add($"NativeUpstreamDTextFullInputAcadUnicodeExpandedText: {Escape(expandedDTextDecoded)}");
                    lines.Add($"NativeUpstreamDTextFullInputAcadUnicodeEscapeCount: {dtextExpandedCount}");
                    lines.Add($"NativeUpstreamDTextFullInputMatchesCurrentAfterAcadUnicodeEscape: {expandedMatchesCurrent}");
                }
            }
            else
            {
                lines.Add($"NativeUpstreamDTextFullInputDecode: <unavailable {dtextDecodeReason}>");
            }

            bool preDecodeRepresentsCurrent = matchesCurrentEncoded
                || matchesCurrentAcadEscaped
                || decodedRepresentsCurrent;
            lines.Add($"NativePreDecodeDTextInputRepresentsCurrentText: {preDecodeRepresentsCurrent}");
            if (matchesObservedCarrier)
            {
                lines.Add("NativePreDecodeDTextInputConclusion: pre-decode-buffer-equals-observed-carrier-current-decode-chain-suspect");
            }
            else if (preDecodeRepresentsCurrent)
            {
                lines.Add("NativePreDecodeDTextInputConclusion: pre-decode-buffer-already-represents-current-text-before-dtext-conversion");
            }
            else
            {
                lines.Add("NativePreDecodeDTextInputConclusion: pre-decode-buffer-unclassified-investigate-earlier-source");
            }
        }
        lines.Add("NativeUpstreamDTextFullInputDecision: diagnostic-only full TextEditor char* input boundary, no-auto-write");
        lines.Add(
            $"NativeUpstreamCursorDeltaStreamLength: {summary.CursorDeltaStreamBytes.Length}" +
            (summary.CursorDeltaStreamTruncated ? "+" : string.Empty));
        if (summary.CursorDeltaStreamBytes.Length > 0)
            lines.Add($"NativeUpstreamCursorDeltaStreamBytes: {FormatBytes(summary.CursorDeltaStreamBytes, 160)}");
        lines.Add($"NativeUpstreamCursorDeltaAmbiguousCount: {summary.CursorDeltaStreamAmbiguousCount}");
        if (summary.CursorDeltaPendingBytes.Length > 0)
            lines.Add($"NativeUpstreamCursorDeltaPendingBytes: {FormatBytes(summary.CursorDeltaPendingBytes, 24)}");
        lines.Add($"NativeUpstreamCursorDeltaCandidateLength: {cursorCandidateBytes.Length}");
        if (cursorCandidateBytes.Length > 0)
            lines.Add($"NativeUpstreamCursorDeltaCandidateBytes: {FormatBytes(cursorCandidateBytes, 160)}");
        lines.Add("NativeUpstreamCursorDeltaCandidateDecision: diagnostic-only stream-plus-pending-prefix-before-nul, no-auto-write");
        lines.Add("NativeUpstreamCursorDeltaStreamDecision: diagnostic-only cursor-delta bytes before native decode, no-auto-write");
        lines.Add($"NativeUpstreamInputSamples: {summary.InputSamples.Length}");
        for (int i = 0; i < summary.InputSamples.Length; i++)
            lines.Add($"NativeUpstreamInputSample[{i}]: {summary.InputSamples[i]}");
        lines.Add("NativeUpstreamDecision: diagnostic-only native decode context, no-auto-write");
        return (cursorCandidateBytes, summary.LastDTextFullInputBytes);
    }

    private static byte[] BuildCursorDeltaCandidateBytes(byte[] streamBytes, byte[] pendingBytes)
    {
        if (streamBytes.Length == 0 && pendingBytes.Length == 0)
            return [];

        var bytes = new List<byte>(streamBytes.Length + pendingBytes.Length);
        bytes.AddRange(streamBytes);
        foreach (byte value in pendingBytes)
        {
            if (value == 0)
                break;

            bytes.Add(value);
        }

        return bytes.ToArray();
    }

    private static void AppendNativeReadStringEvents(List<string> lines, NativeReadStringEvent[] events)
    {
        lines.Add($"NativeReadStringEvents: {events.Length}");
        int count = Math.Min(events.Length, 8);
        for (int i = 0; i < count; i++)
        {
            NativeReadStringEvent item = events[i];
            lines.Add(
                $"NativeReadString[{i}]: {item.OverloadName}, return=0x{item.ReturnRva:X}, " +
                $"status={item.Status}, dbcs={item.IsDoubleByte}, " +
                $"pos={item.StartByteOffset}:{item.StartBitOffset}->{item.EndByteOffset}:{item.EndBitOffset}, " +
                $"raw={FormatBytes(item.RawBytes, 48)}, text='{Escape(item.Text)}'");
        }
    }

    private static void AppendNativeFilerVirtualSnapshot(List<string> lines, NativeFilerVirtualSnapshot snapshot)
    {
        if (snapshot.VTableRva == 0)
        {
            lines.Add("NativeFilerVTable: <unavailable>");
            return;
        }

        lines.Add($"NativeFilerVTable: rva=0x{snapshot.VTableRva:X}");
        lines.Add(
            "NativeFilerVirtualMethods: " +
            $"+0x40=0x{snapshot.Method40Rva:X}, " +
            $"+0x88=0x{snapshot.Method88Rva:X}, " +
            $"+0xB8=0x{snapshot.MethodB8Rva:X}, " +
            $"+0xC0=0x{snapshot.MethodC0Rva:X}, " +
            $"+0xC8=0x{snapshot.MethodC8Rva:X}, " +
            $"+0x188=0x{snapshot.Method188Rva:X}, " +
            $"+0x280=0x{snapshot.Method280Rva:X}, " +
            $"+0x290=0x{snapshot.Method290Rva:X}");
        lines.Add("NativeFilerVirtualDecision: diagnostic-only vtable targets for upstream reader selection, no-auto-write");
    }

    private static void AppendNativeTextSetSourceEvents(
        List<string> lines,
        NativeTextSetSourceEvent[] events,
        string currentText,
        string nativeText)
    {
        lines.Add($"NativeTextSetSourceEvents: {events.Length}");
        int matchesCurrentCount = 0;
        int differsFromCurrentCount = 0;
        foreach (NativeTextSetSourceEvent item in events)
        {
            if (string.Equals(item.Text, currentText, StringComparison.Ordinal))
                matchesCurrentCount++;
            else
                differsFromCurrentCount++;
        }

        int count = Math.Min(events.Length, 8);
        for (int i = 0; i < count; i++)
        {
            NativeTextSetSourceEvent item = events[i];
            bool matchesCurrent = string.Equals(item.Text, currentText, StringComparison.Ordinal);
            lines.Add(
                $"NativeTextSetSource[{i}]: return=0x{item.ReturnRva:X}, src=0x{item.SourcePointer.ToInt64():X}, " +
                $"dst=+0x{item.DestinationOffset:X}, len={item.Text.Length}{(item.IsTruncated ? "+" : "")}, " +
                $"matchesCurrent={matchesCurrent}, " +
                $"matchesNative={string.Equals(item.Text, nativeText, StringComparison.Ordinal)}, " +
                $"text='{Escape(item.Text)}'");
        }

        if (events.Length == 0)
        {
            lines.Add("NativeTextSetSourceConclusion: no-source-event-captured");
        }
        else if (differsFromCurrentCount == 0 && matchesCurrentCount == events.Length)
        {
            lines.Add("NativeTextSetSourceConclusion: source-already-current-text, setter-not-root-cause");
        }
        else
        {
            lines.Add("NativeTextSetSourceConclusion: source-differs-from-current-text, investigate-post-setter-mutation");
        }

        lines.Add("NativeTextSetSourceDecision: diagnostic-only complete UTF-16 source before AcDbImpText text assignment, no-auto-write");
    }

    private static void AppendNativeDwgInRawSnapshot(
        List<string> lines,
        NativeDwgInRawSnapshot snapshot,
        int codePageId,
        string currentText,
        string nativeText,
        byte[] upstreamCursorCandidateBytes,
        byte[] upstreamDTextFullInputBytes)
    {
        lines.Add(
            $"NativeDwgInRawRange: {FormatPosition(snapshot.StartByteOffset, snapshot.StartBitOffset)}->" +
            $"{FormatPosition(snapshot.EndByteOffset, snapshot.EndBitOffset)}");
        lines.Add(
            $"NativeDwgInRawBytes: {FormatBytes(snapshot.RawBytes, 160)}" +
            (snapshot.IsTruncated ? " ..." : string.Empty));
        AppendNativeDwgTerminalText(
            lines,
            snapshot,
            codePageId,
            currentText,
            nativeText,
            upstreamCursorCandidateBytes,
            upstreamDTextFullInputBytes);
        lines.Add("NativeDwgInRawDecision: diagnostic-only bitstream, not direct text evidence");
    }

    private static void AppendNativeDwgTerminalText(
        List<string> lines,
        NativeDwgInRawSnapshot snapshot,
        int codePageId,
        string currentText,
        string nativeText,
        byte[] upstreamCursorCandidateBytes,
        byte[] upstreamDTextFullInputBytes)
    {
        if (!DbTextEncodingRepairService.TryExtractTerminalDwgTextField(
                snapshot,
                out byte[] terminalBytes,
                out int lengthMarkerOffset,
                out int prefixByte,
                out string extractReason))
        {
            lines.Add($"NativeDwgTerminalDecision: unavailable {extractReason}");
            lines.Add("NativeDwgRootCauseConclusion: terminal-field-unavailable-cannot-classify-reader-boundary");
            return;
        }

        lines.Add(
            $"NativeDwgTerminalLength: offset={lengthMarkerOffset}, prefix={FormatOptionalByte(prefixByte)}, " +
            $"declared={terminalBytes.Length + 1}, payload={terminalBytes.Length}");
        lines.Add($"NativeDwgTerminalBytes: {FormatBytes(terminalBytes, 96)}");
        bool terminalMatchesCurrentEncoded = false;
        bool terminalRepresentsCurrentText = false;
        if (DbTextEncodingRepairService.TryEncodeDwgTextWithCodePage(
                currentText,
                codePageId,
                out byte[] currentEncodedBytes,
                out int currentEncodedWindowsCodePage,
                out string currentEncodeReason))
        {
            terminalMatchesCurrentEncoded = terminalBytes.SequenceEqual(currentEncodedBytes);
            terminalRepresentsCurrentText = terminalMatchesCurrentEncoded;
            lines.Add($"CurrentTextEncodedByObjectCodePage: CP{currentEncodedWindowsCodePage} => {FormatBytes(currentEncodedBytes, 96)}");
            lines.Add($"NativeDwgTerminalMatchesCurrentEncodedByObjectCodePage: {terminalMatchesCurrentEncoded}");
        }
        else
        {
            lines.Add($"CurrentTextEncodedByObjectCodePage: unavailable {currentEncodeReason}");
            lines.Add("NativeDwgTerminalMatchesCurrentEncodedByObjectCodePage: <unavailable>");
        }

        lines.Add(upstreamCursorCandidateBytes.Length > 0
            ? $"NativeUpstreamCursorDeltaCandidateMatchesTerminalBytes: {upstreamCursorCandidateBytes.SequenceEqual(terminalBytes)}"
            : "NativeUpstreamCursorDeltaCandidateMatchesTerminalBytes: <none>");
        lines.Add(upstreamDTextFullInputBytes.Length > 0
            ? $"NativeUpstreamDTextFullInputMatchesTerminalBytes: {upstreamDTextFullInputBytes.SequenceEqual(terminalBytes)}"
            : "NativeUpstreamDTextFullInputMatchesTerminalBytes: <none>");
        if (upstreamDTextFullInputBytes.Length > 0
            && upstreamCursorCandidateBytes.Length > 0
            && upstreamDTextFullInputBytes.SequenceEqual(terminalBytes)
            && upstreamCursorCandidateBytes.SequenceEqual(terminalBytes))
        {
            lines.Add("NativeTextEditorChainConclusion: closed full-input/cursor/terminal payloads are identical");
        }

        if (DbTextEncodingRepairService.TryDecodeDwgTextBytesWithCodePage(
                terminalBytes,
                codePageId,
                out string decoded,
                out int windowsCodePage,
                out string decodeReason))
        {
            bool matchesCurrent = string.Equals(decoded, currentText, StringComparison.Ordinal);
            bool matchesNativeText = string.Equals(decoded, nativeText, StringComparison.Ordinal);
            terminalRepresentsCurrentText |= matchesCurrent;
            lines.Add(
                $"NativeDwgTerminalDecode: CP{windowsCodePage} " +
                $"({DwgFilerCodePageScopeHook.FormatCodePageId(codePageId)}) => {Escape(decoded)}");
            lines.Add($"NativeDwgTerminalMatchesCurrent: {matchesCurrent}");
            lines.Add($"NativeDwgTerminalMatchesNativeText: {matchesNativeText}");

            if (TryExpandAcadUnicodeEscapes(decoded, out string expandedDecoded, out int expandedCount))
            {
                bool expandedMatchesCurrent = string.Equals(expandedDecoded, currentText, StringComparison.Ordinal);
                bool expandedMatchesNativeText = string.Equals(expandedDecoded, nativeText, StringComparison.Ordinal);
                terminalRepresentsCurrentText |= expandedMatchesCurrent;
                lines.Add($"NativeDwgTerminalAcadUnicodeExpandedText: {Escape(expandedDecoded)}");
                lines.Add($"NativeDwgTerminalAcadUnicodeEscapeCount: {expandedCount}");
                lines.Add($"NativeDwgTerminalMatchesCurrentAfterAcadUnicodeEscape: {expandedMatchesCurrent}");
                lines.Add($"NativeDwgTerminalMatchesNativeTextAfterAcadUnicodeEscape: {expandedMatchesNativeText}");
            }

            lines.Add(terminalRepresentsCurrentText
                ? "NativeDwgTerminalRepairDecision: diagnostic-only terminal-represents-current-text-not-auto-blocking"
                : "NativeDwgTerminalRepairDecision: diagnostic-only terminal-text-mismatch");

            if (DbTextEncodingRepairService.TryGetAlternateDbcsWindowsCodePage(
                    windowsCodePage,
                    out int alternateWindowsCodePage))
            {
                if (DbTextEncodingRepairService.TryDecodeDwgTextBytesWithWindowsCodePage(
                        terminalBytes,
                        alternateWindowsCodePage,
                        out string alternateDecoded,
                        out string alternateReason))
                {
                    lines.Add($"NativeDwgTerminalAlternateDecode: CP{alternateWindowsCodePage} => {Escape(alternateDecoded)}");
                }
                else
                {
                    lines.Add($"NativeDwgTerminalAlternateDecode: CP{alternateWindowsCodePage} unavailable {alternateReason}");
                }
            }
        }
        else
        {
            lines.Add($"NativeDwgTerminalDecode: unavailable {decodeReason}");
        }

        AppendNativeDwgRootCauseConclusion(lines, snapshot, terminalBytes, currentText, terminalRepresentsCurrentText);
        lines.Add("NativeDwgTerminalDecision: diagnostic-only terminal length field, no-auto-write");
    }

    private static void AppendNativeDwgRootCauseConclusion(
        List<string> lines,
        NativeDwgInRawSnapshot snapshot,
        byte[] terminalBytes,
        string currentText,
        bool terminalRepresentsCurrentText)
    {
        if (!terminalRepresentsCurrentText)
        {
            lines.Add("NativeDwgRootCauseConclusion: terminal-payload-does-not-equal-current-text-recheck-reader-boundary");
            return;
        }

        if (!TextEditorDbcsDecodeHook.TryBuildObservedCarrierBytes(
                currentText,
                out byte[] carrierBytes,
                out string carrierReason))
        {
            lines.Add($"NativeDwgRootCauseObservedCarrier: unavailable {carrierReason}");
            lines.Add("NativeDwgRootCauseConclusion: terminal-payload-equals-current-text-no-independent-carrier");
            return;
        }

        bool carrierMatchesTerminal = carrierBytes.SequenceEqual(terminalBytes);
        int carrierIndexInRaw = snapshot.RawBytes.Length == 0 ? -1 : IndexOfSequence(snapshot.RawBytes, carrierBytes);
        lines.Add($"NativeDwgRootCauseObservedCarrierMatchesTerminal: {carrierMatchesTerminal}");
        lines.Add(snapshot.RawBytes.Length > 0
            ? $"NativeDwgRootCauseObservedCarrierIndexInObjectRaw: {carrierIndexInRaw}"
            : "NativeDwgRootCauseObservedCarrierIndexInObjectRaw: <none>");

        if (!carrierMatchesTerminal && carrierIndexInRaw < 0)
        {
            lines.Add(
                "NativeDwgRootCauseConclusion: current-dwg-terminal-payload-already-equals-current-garbled-text; " +
                "observed-carrier-not-present-in-object-raw-window");
            return;
        }

        if (carrierMatchesTerminal)
        {
            lines.Add("NativeDwgRootCauseConclusion: observed-carrier-is-terminal-payload-current-load-decode-boundary-still-suspect");
            return;
        }

        lines.Add("NativeDwgRootCauseConclusion: observed-carrier-present-in-object-raw-window-investigate-earlier-reader");
    }

    private static bool TryBuildAcadUnicodeEscapedBytes(
        string value,
        int codePageId,
        out byte[] bytes,
        out int windowsCodePage,
        out string reason)
    {
        bytes = [];
        windowsCodePage = 0;

        string escaped = EscapePrivateUseAsAcadUnicode(value, out int replacementCount);
        if (replacementCount == 0)
        {
            reason = "no-private-use";
            return false;
        }

        return DbTextEncodingRepairService.TryEncodeDwgTextWithCodePage(
            escaped,
            codePageId,
            out bytes,
            out windowsCodePage,
            out reason);
    }

    private static string EscapePrivateUseAsAcadUnicode(string value, out int replacementCount)
    {
        replacementCount = 0;
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (ch >= '\uE000' && ch <= '\uF8FF')
            {
                builder.Append("\\U+");
                builder.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                replacementCount++;
                continue;
            }

            builder.Append(ch);
        }

        return replacementCount == 0 ? value : builder.ToString();
    }

    private static bool TryExpandAcadUnicodeEscapes(string value, out string expanded, out int replacementCount)
    {
        expanded = value;
        replacementCount = 0;

        int markerIndex = value.IndexOf("\\U+", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        var builder = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length;)
        {
            if (i + 6 < value.Length
                && value[i] == '\\'
                && (value[i + 1] == 'U' || value[i + 1] == 'u')
                && value[i + 2] == '+'
                && IsHexDigit(value[i + 3])
                && IsHexDigit(value[i + 4])
                && IsHexDigit(value[i + 5])
                && IsHexDigit(value[i + 6]))
            {
                int codePoint =
                    (HexValue(value[i + 3]) << 12)
                    | (HexValue(value[i + 4]) << 8)
                    | (HexValue(value[i + 5]) << 4)
                    | HexValue(value[i + 6]);
                builder.Append((char)codePoint);
                replacementCount++;
                i += 7;
                continue;
            }

            builder.Append(value[i]);
            i++;
        }

        expanded = builder.ToString();
        return replacementCount > 0;
    }

    private static bool IsHexDigit(char ch)
    {
        return (ch >= '0' && ch <= '9')
            || (ch >= 'A' && ch <= 'F')
            || (ch >= 'a' && ch <= 'f');
    }

    private static int HexValue(char ch)
    {
        if (ch >= '0' && ch <= '9')
            return ch - '0';

        if (ch >= 'A' && ch <= 'F')
            return ch - 'A' + 10;

        return ch - 'a' + 10;
    }

    private static void AppendTablePreview(List<string> lines, Table table)
    {
        int emitted = 0;
        for (int row = 0; row < table.Rows.Count && emitted < 20; row++)
        {
            for (int col = 0; col < table.Columns.Count && emitted < 20; col++)
            {
                string text = Safe(() => table.Cells[row, col].TextString);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                lines.Add($"Table[{row},{col}].TextString: {Escape(text)}");
                emitted++;
            }
        }
    }

    private static void AppendObjectBoundMetadataStrings(List<string> lines, DBObject obj, Transaction tr)
    {
        List<string> values = CollectObjectBoundMetadataStrings(obj, tr);

        lines.Add($"ObjectBoundMetadataStringCount: {values.Count}");
        int count = Math.Min(values.Count, 24);
        for (int i = 0; i < count; i++)
            lines.Add($"ObjectBoundMetadataString[{i}]: {Escape(values[i])}");

        lines.Add(values.Count == 0
            ? "ObjectBoundMetadataStringConclusion: no-object-bound-redundant-source"
            : "ObjectBoundMetadataStringConclusion: object-bound-strings-present-review-manually");
        lines.Add("ObjectBoundMetadataStringDecision: diagnostic-only object-bound XData/ExtensionDictionary strings, no-auto-write");

        List<MetadataObjectReference> references = CollectObjectBoundMetadataReferences(obj, tr);
        List<string> linkedValues = CollectLinkedObjectStrings(obj, tr, references, out int resolvedObjects, out int errors);
        lines.Add($"LinkedObjectMetadataReferenceCount: {references.Count}");
        lines.Add($"LinkedObjectMetadataResolvedCount: {resolvedObjects}");
        lines.Add($"LinkedObjectMetadataStringCount: {linkedValues.Count}");
        if (errors > 0)
            lines.Add($"LinkedObjectMetadataErrorCount: {errors}");

        int linkedCount = Math.Min(linkedValues.Count, 24);
        for (int i = 0; i < linkedCount; i++)
            lines.Add($"LinkedObjectMetadataString[{i}]: {Escape(linkedValues[i])}");

        lines.Add(linkedValues.Count == 0
            ? "LinkedObjectMetadataConclusion: no-linked-object-redundant-source"
            : "LinkedObjectMetadataConclusion: linked-object-strings-present-review-static-meaning");
        lines.Add("LinkedObjectMetadataDecision: diagnostic-only referenced object strings, no-auto-write");
    }

    private static List<string> CollectObjectBoundMetadataStrings(DBObject obj, Transaction tr)
    {
        var values = new List<string>();
        AppendXDataStrings(values, obj);
        AppendExtensionDictionaryStrings(values, obj, tr);
        return values;
    }

    private static List<MetadataObjectReference> CollectObjectBoundMetadataReferences(DBObject obj, Transaction tr)
    {
        var references = new List<MetadataObjectReference>();
        AppendXDataReferences(references, obj);
        AppendExtensionDictionaryReferences(references, obj, tr);
        return references;
    }

    private static void AppendXDataStrings(List<string> values, DBObject obj)
    {
        ResultBuffer? xdata = null;
        try
        {
            xdata = obj.XData;
            AppendResultBufferStrings(values, "XData", xdata);
        }
        catch (System.Exception ex)
        {
            values.Add($"XData:<unavailable {ex.GetType().Name}>");
        }
        finally
        {
            xdata?.Dispose();
        }
    }

    private static void AppendXDataReferences(List<MetadataObjectReference> references, DBObject obj)
    {
        ResultBuffer? xdata = null;
        try
        {
            xdata = obj.XData;
            AppendResultBufferReferences(references, "XData", xdata, obj.Database);
        }
        catch
        {
            // Reference scanning is diagnostic-only; string diagnostics report unavailable XData separately.
        }
        finally
        {
            xdata?.Dispose();
        }
    }

    private static void AppendExtensionDictionaryStrings(List<string> values, DBObject obj, Transaction tr)
    {
        try
        {
            if (obj.ExtensionDictionary.IsNull)
                return;

            if (tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead, false, true) is DBDictionary dictionary)
                AppendDictionaryStrings(values, "ExtensionDictionary", dictionary, tr, 0);
        }
        catch (System.Exception ex)
        {
            values.Add($"ExtensionDictionary:<unavailable {ex.GetType().Name}>");
        }
    }

    private static void AppendExtensionDictionaryReferences(
        List<MetadataObjectReference> references,
        DBObject obj,
        Transaction tr)
    {
        try
        {
            if (obj.ExtensionDictionary.IsNull)
                return;

            if (tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead, false, true) is DBDictionary dictionary)
                AppendDictionaryReferences(references, "ExtensionDictionary", dictionary, tr, 0);
        }
        catch
        {
            // Diagnostic-only path; ignore unreadable dictionaries here.
        }
    }

    private static void AppendDictionaryStrings(
        List<string> values,
        string scope,
        DBDictionary dictionary,
        Transaction tr,
        int depth)
    {
        if (depth > 2 || values.Count >= 64)
            return;

        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (values.Count >= 64)
                return;

            DBObject child;
            try
            {
                child = tr.GetObject(entry.Value, OpenMode.ForRead, false, true);
            }
            catch (System.Exception ex)
            {
                values.Add($"{scope}.{entry.Key}:<unavailable {ex.GetType().Name}>");
                continue;
            }

            if (child is Xrecord xrecord)
            {
                ResultBuffer? data = null;
                try
                {
                    data = xrecord.Data;
                    AppendResultBufferStrings(values, $"{scope}.{entry.Key}", data);
                }
                finally
                {
                    data?.Dispose();
                }
            }
            else if (child is DBDictionary nested)
            {
                AppendDictionaryStrings(values, $"{scope}.{entry.Key}", nested, tr, depth + 1);
            }
        }
    }

    private static void AppendDictionaryReferences(
        List<MetadataObjectReference> references,
        string scope,
        DBDictionary dictionary,
        Transaction tr,
        int depth)
    {
        if (depth > 2 || references.Count >= 64)
            return;

        foreach (DBDictionaryEntry entry in dictionary)
        {
            if (references.Count >= 64)
                return;

            DBObject child;
            try
            {
                child = tr.GetObject(entry.Value, OpenMode.ForRead, false, true);
            }
            catch
            {
                continue;
            }

            if (child is Xrecord xrecord)
            {
                ResultBuffer? data = null;
                try
                {
                    data = xrecord.Data;
                    AppendResultBufferReferences(references, $"{scope}.{entry.Key}", data, child.Database);
                }
                finally
                {
                    data?.Dispose();
                }
            }
            else if (child is DBDictionary nested)
            {
                AppendDictionaryReferences(references, $"{scope}.{entry.Key}", nested, tr, depth + 1);
            }
        }
    }

    private static void AppendResultBufferStrings(List<string> values, string scope, ResultBuffer? buffer)
    {
        if (buffer == null)
            return;

        foreach (TypedValue item in buffer)
        {
            if (item.Value is not string text || string.IsNullOrWhiteSpace(text))
                continue;

            values.Add($"{scope}[{item.TypeCode}]: {text}");
            if (values.Count >= 64)
                return;
        }
    }

    private static void AppendResultBufferReferences(
        List<MetadataObjectReference> references,
        string scope,
        ResultBuffer? buffer,
        Database? db)
    {
        if (buffer == null || db == null)
            return;

        foreach (TypedValue item in buffer)
        {
            if (references.Count >= 64)
                return;

            if (item.Value is ObjectId objectId && !objectId.IsNull)
            {
                references.Add(new MetadataObjectReference(scope, $"{item.TypeCode}:{objectId}", objectId));
                continue;
            }

            if (item.Value is Handle handle
                && TryResolveHandle(db, handle, out ObjectId handleObjectId))
            {
                references.Add(new MetadataObjectReference(scope, $"{item.TypeCode}:{handle}", handleObjectId));
                continue;
            }

            if (item.TypeCode == 1005
                && item.Value is string handleText
                && TryResolveHandle(db, handleText, out ObjectId xdataObjectId))
            {
                references.Add(new MetadataObjectReference(scope, $"{item.TypeCode}:{handleText}", xdataObjectId));
            }
        }
    }

    private static List<string> CollectLinkedObjectStrings(
        DBObject source,
        Transaction tr,
        IReadOnlyList<MetadataObjectReference> references,
        out int resolvedObjects,
        out int errors)
    {
        var values = new List<string>();
        var visited = new HashSet<ObjectId>();
        resolvedObjects = 0;
        errors = 0;

        foreach (MetadataObjectReference reference in references)
        {
            if (reference.ObjectId.IsNull
                || reference.ObjectId == source.ObjectId
                || !visited.Add(reference.ObjectId))
            {
                continue;
            }

            DBObject linkedObject;
            try
            {
                linkedObject = tr.GetObject(reference.ObjectId, OpenMode.ForRead, false, true);
                resolvedObjects++;
            }
            catch
            {
                errors++;
                continue;
            }

            AppendLinkedObjectStrings(values, reference, linkedObject, tr);
            if (values.Count >= 128)
                break;
        }

        return values;
    }

    private static void AppendLinkedObjectStrings(
        List<string> values,
        MetadataObjectReference reference,
        DBObject linkedObject,
        Transaction tr)
    {
        string prefix =
            $"LinkedObject[{reference.Scope}:{Escape(reference.Display)}:{Safe(() => linkedObject.Handle.ToString())}]";

        if (linkedObject is DBText linkedDbText)
            AppendNonEmpty(values, $"{prefix}.DBText.TextString", linkedDbText.TextString);
        else if (linkedObject is MText linkedMText)
            AppendNonEmpty(values, $"{prefix}.MText.Contents", Safe(() => linkedMText.Contents ?? string.Empty));

        foreach (string metadataValue in CollectObjectBoundMetadataStrings(linkedObject, tr))
            AppendNonEmpty(values, $"{prefix}.{ExtractMetadataName(metadataValue)}", ExtractMetadataPayload(metadataValue));
    }

    private static void AppendNonEmpty(List<string> values, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        values.Add($"{name}: {value}");
    }

    private static bool TryResolveHandle(Database db, string handleText, out ObjectId objectId)
    {
        objectId = ObjectId.Null;
        string normalized = handleText.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        if (!long.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            return false;

        return TryResolveHandle(db, new Handle(value), out objectId);
    }

    private static bool TryResolveHandle(Database db, Handle handle, out ObjectId objectId)
    {
        objectId = ObjectId.Null;
        try
        {
            objectId = db.GetObjectId(false, handle, 0);
            return !objectId.IsNull;
        }
        catch
        {
            objectId = ObjectId.Null;
            return false;
        }
    }

    private static void AppendStringProperties(List<string> lines, DBObject obj)
    {
        var properties = obj.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0 && p.PropertyType == typeof(string))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var property in properties)
        {
            string value = Safe(() => (string?)property.GetValue(obj) ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            lines.Add($"Property.{property.Name}: {Escape(value)}");
        }
    }

    private static string DescribeTextStyle(ObjectId styleId, Transaction tr)
    {
        if (styleId.IsNull || styleId.IsErased)
            return "<null>";

        var style = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead, false, true);
        return $"Name='{style.Name}', FileName='{style.FileName}', BigFont='{style.BigFontFileName}', TypeFace='{style.Font.TypeFace}'";
    }

    private static void AppendCjkShxDisplayBoundary(
        List<string> lines,
        string text,
        ObjectId styleId,
        Transaction tr)
    {
        if (string.IsNullOrEmpty(text) || !ContainsCjkUnifiedIdeograph(text))
            return;

        if (styleId.IsNull || styleId.IsErased)
            return;

        var style = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead, false, true);
        string fileName = style.FileName ?? string.Empty;
        string bigFont = style.BigFontFileName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(bigFont))
            return;

        bool shxMain = IsShxStyleFont(fileName);
        bool shxBig = IsShxStyleFont(bigFont);
        if (!shxMain && !shxBig)
            return;

        lines.Add(
            "DisplayGlyphBoundary: cjk-text-uses-shx-bigfont; " +
            "if TextString is already correct but glyph shape is wrong, handle as font/style replacement, not DBText encoding repair");
    }

    private static bool ContainsCjkUnifiedIdeograph(string text)
    {
        foreach (char ch in text)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF')
                return true;
        }

        return false;
    }

    private static bool IsShxStyleFont(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        return fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            || !System.IO.Path.HasExtension(fontName);
    }

    private sealed class MetadataObjectReference
    {
        public MetadataObjectReference(string scope, string display, ObjectId objectId)
        {
            Scope = scope;
            Display = display;
            ObjectId = objectId;
        }

        public string Scope { get; }

        public string Display { get; }

        public ObjectId ObjectId { get; }
    }

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (System.Exception ex) { return "<读取失败:" + ex.GetType().Name + ":" + ex.Message + ">"; }
    }

    private static bool IsMetadataUnavailableDiagnostic(string value)
    {
        return value.Contains("<unavailable", StringComparison.Ordinal);
    }

    private static bool IsKnownStructuralMetadataString(string value)
    {
        if (value.StartsWith("XData[1001]:", StringComparison.Ordinal)
            || value.StartsWith("XData[1002]:", StringComparison.Ordinal)
            || value.StartsWith("XData[1003]:", StringComparison.Ordinal)
            || value.StartsWith("XData[1005]:", StringComparison.Ordinal))
        {
            return true;
        }

        if (!value.StartsWith("XData[1000]:", StringComparison.Ordinal))
            return false;

        string payload = ExtractMetadataPayload(value);
        return string.Equals(payload, "ACAD_MTEXT_DEFINED_HEIGHT_BEGIN", StringComparison.Ordinal)
            || string.Equals(payload, "ACAD_MTEXT_DEFINED_HEIGHT_END", StringComparison.Ordinal);
    }

    private static string ExtractMetadataPayload(string value)
    {
        int index = value.IndexOf("]: ", StringComparison.Ordinal);
        if (index >= 0)
            return value[(index + 3)..];

        index = value.IndexOf(": ", StringComparison.Ordinal);
        return index >= 0 ? value[(index + 2)..] : value;
    }

    private static string ExtractMetadataName(string value)
    {
        int index = value.IndexOf("]: ", StringComparison.Ordinal);
        if (index >= 0)
            return value[..(index + 1)];

        index = value.IndexOf(": ", StringComparison.Ordinal);
        return index >= 0 ? value[..index] : value;
    }

    private static string TrimForReport(string value)
    {
        const int maxChars = 96;
        return value.Length <= maxChars ? value : value[..maxChars] + "...";
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string FormatBytes(byte[] bytes, int limit)
    {
        if (bytes.Length == 0)
            return "<empty>";

        int count = Math.Min(bytes.Length, limit);
        var parts = new string[count];
        for (int i = 0; i < count; i++)
            parts[i] = bytes[i].ToString("X2");

        return bytes.Length <= limit
            ? string.Join(" ", parts)
            : string.Join(" ", parts) + " ...";
    }

    private static string FormatPosition(int byteOffset, int bitOffset)
    {
        return byteOffset < 0 || bitOffset < 0
            ? "<none>"
            : $"{byteOffset}:{bitOffset}";
    }

    private static string FormatOptionalByte(int value)
    {
        return value < 0 ? "<none>" : value.ToString("X2");
    }
}
#endif
