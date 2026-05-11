#if DEBUG
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.FontMapping;

namespace AFR.Services;

/// <summary>
/// DBText native 解码证据修复服务。
/// <para>
/// 只在 DEBUG 构建中启用。native 证据写回仍要求对象级 provenance 精确匹配；
/// observed fallback 只允许在用户声明的“简体中文工程图，允许英文混排”域策略下写回。
/// </para>
/// </summary>
internal static class DbTextEncodingRepairService
{
    private const int CandidateLogLimit = 80;
    private const int SkipLogLimit = 40;
    private const int InvalidDecodedLogLimit = 40;
    private const int MaxLoggedTextChars = 80;
    private static readonly bool EnableNativeImpTextIndependentSourceProbe = false;
    private static readonly bool EnableSimplifiedChineseEngineeringPolicyRepair = true;
    private static readonly Lazy<HashSet<char>> Gb2312CommonCharSet = new(BuildGb2312CommonCharSet);

    /// <summary>
    /// 修复当前数据库中具备 native 对象级证据的 <see cref="DBText"/> 文本。
    /// <para>
    /// 不使用字体显示或问号外观判断；只有 <c>DBText.UnmanagedObject + 0x8</c> 指向的 native
    /// <c>AcDbImpText</c> 与 DWG 读入阶段记录的 <c>AcDbImpText</c> 相同，且 native
    /// 读入后文本与当前 TextString 完全一致时才进入修复判断。
/// </para>
    /// </summary>
    /// <param name="db">当前 AutoCAD 图纸数据库。</param>
    /// <returns>实际写回的 DBText 数量。</returns>
    public static int Repair(Database db)
    {
        if (db == null)
            return 0;

        if (DbTextHookTracer.IsEnabled)
        {
            DiagnosticLogger.Log("DBText证据修复", "已跳过: DbTextHookTracer 处于只读追踪模式。");
            return 0;
        }

        int scanned = 0;
        int eligiblePreview = 0;
        int skipped = 0;
        int invalidDecoded = 0;
        int errors = 0;
        int missingProvenance = 0;
        int provenanceMismatch = 0;
        int missingProvenanceLogCount = 0;
        int partialSkipLogCount = 0;
        int exactNativeSequenceCount = 0;
        int observedDiagnosticOnlyCount = 0;
        int observedDiagnosticLogCount = 0;
        int observedBlockedByNativeExactCount = 0;
        int observedBlockedLogCount = 0;
        int independentWideSourceDiagnosticCount = 0;
        int independentCarrierSourceDiagnosticCount = 0;
        int privateUseSymbolPreservedCount = 0;
        int simplifiedPolicyAcceptedCount = 0;
        int simplifiedPolicyRejectedCount = 0;
        int simplifiedPolicyRepairedCount = 0;
        int repaired = 0;

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId blockId in blockTable)
        {
            BlockTableRecord block;
            try
            {
                block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                if (block.IsFromExternalReference || block.IsDependent)
                    continue;
            }
            catch (Exception ex)
            {
                errors++;
                DiagnosticLogger.Log("DBText证据修复", $"跳过块记录: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (ObjectId entityId in block)
            {
                try
                {
                    if (tr.GetObject(entityId, OpenMode.ForRead, false, true) is not DBText dbText)
                        continue;

                    scanned++;
                    string original = dbText.TextString ?? string.Empty;
                    if (!TryGetNativeImpTextPointer(dbText, out IntPtr impText)
                        || !DbTextDwgInFieldsScopeHook.TryGetProvenance(impText, out NativeDbTextProvenance provenance))
                    {
                        missingProvenance++;
                        skipped++;
                        if (missingProvenanceLogCount++ < SkipLogLimit)
                        {
                            DiagnosticLogger.Log(
                                "DBText证据修复",
                                $"跳过 Handle={dbText.Handle}: missing-native-object-provenance, " +
                                $"ImpText=0x{impText.ToInt64():X}, Text='{EscapeForLog(TrimForLog(original))}'");
                        }

                        continue;
                    }

                    if (!string.Equals(provenance.NativeText, original, StringComparison.Ordinal))
                    {
                        provenanceMismatch++;
                        skipped++;
                        if (provenanceMismatch <= SkipLogLimit)
                        {
                            DiagnosticLogger.Log(
                                "DBText证据修复",
                                $"跳过 Handle={dbText.Handle}: native provenance text mismatch, " +
                                $"Native='{EscapeForLog(provenance.NativeText)}', Current='{EscapeForLog(original)}'");
                        }

                        continue;
                    }

                    bool allowPrivateUse = false;
                    bool repairBySimplifiedPolicy = false;
                    string decoded;
                    string reason;
                    if (TryBuildTextFromNativeDbcsSequence(
                            original,
                            provenance.NativeDbcsDecodedText,
                            out decoded,
                            out reason))
                    {
                        allowPrivateUse = true;
                        exactNativeSequenceCount++;
                    }
                    else
                    {
                        string nativeReason = reason;
                        if (TryGetObservedFallbackDiagnosticCandidate(
                                original,
                                provenance.CodePageId,
                                out string observedCandidate,
                                out string observedCandidateReason))
                        {
                            string independentSourceSummary = "disabled-pending-static-offset-verification";
                            if (EnableNativeImpTextIndependentSourceProbe)
                            {
                                NativeImpTextIndependentSourceReport independentReport =
                                    InspectNativeIndependentSource(impText, original, observedCandidate);
                                if (independentReport.IndependentCandidateWideSlotCount > 0)
                                    independentWideSourceDiagnosticCount++;
                                if (independentReport.CandidateCarrierByteSlotCount > 0)
                                    independentCarrierSourceDiagnosticCount++;
                                independentSourceSummary = FormatIndependentSourceSummary(independentReport);
                            }

                            if (EnableSimplifiedChineseEngineeringPolicyRepair
                                && TryAcceptSimplifiedChineseEngineeringPolicyCandidate(
                                    original,
                                    observedCandidate,
                                    out string policyReason))
                            {
                                decoded = observedCandidate;
                                reason =
                                    $"simplified-cn-engineering-policy, NativeEvidence={nativeReason}, " +
                                    $"ObservedCoverage={observedCandidateReason}, {policyReason}";
                                allowPrivateUse = false;
                                repairBySimplifiedPolicy = true;
                                simplifiedPolicyAcceptedCount++;
                            }
                            else
                            {
                                string policyRejectReason = EnableSimplifiedChineseEngineeringPolicyRepair
                                    ? GetSimplifiedChineseEngineeringPolicyRejectReason(original, observedCandidate)
                                    : "policy-disabled";
                                simplifiedPolicyRejectedCount++;
                                observedDiagnosticOnlyCount++;
                                if (observedDiagnosticLogCount++ < SkipLogLimit)
                                {
                                    DiagnosticLogger.Log(
                                        "DBText证据修复",
                                        $"诊断 Handle={dbText.Handle}: observed fallback available but no-auto-write, " +
                                        $"NativeEvidence={nativeReason}, " +
                                        $"PolicyReject={policyRejectReason}, " +
                                        $"CodePage={DwgFilerCodePageScopeHook.FormatCodePageId(provenance.CodePageId)}, " +
                                        $"ReadStringEvents={provenance.ReadStringEvents.Length}, " +
                                        $"DwgInRaw={FormatDwgInRawSummary(provenance.DwgInRaw, provenance.CodePageId)}, " +
                                        $"IndependentSource={independentSourceSummary}, " +
                                        $"ObservedCoverage={observedCandidateReason}, Current='{EscapeForLog(TrimForLog(original))}', " +
                                        $"Observed='{EscapeForLog(TrimForLog(observedCandidate))}'");
                                }
                            }
                        }

                        if (!repairBySimplifiedPolicy)
                        {
                            skipped++;
                            if (partialSkipLogCount++ < SkipLogLimit && nativeReason.StartsWith("partial", StringComparison.Ordinal))
                            {
                                DiagnosticLogger.Log(
                                    "DBText证据修复",
                                    $"跳过 Handle={dbText.Handle}: {nativeReason}, " +
                                    $"Text='{EscapeForLog(TrimForLog(original))}'");
                            }

                            continue;
                        }
                    }

                    if (!allowPrivateUse
                        && TryNormalizePrivateUseSymbolsFromOriginal(
                            original,
                            decoded,
                            out string normalizedDecoded,
                            out string normalizeReason))
                    {
                        decoded = normalizedDecoded;
                        reason += ", " + normalizeReason;
                        privateUseSymbolPreservedCount++;
                    }

                    if (string.Equals(original, decoded, StringComparison.Ordinal))
                    {
                        bool promotedBySimplifiedPolicy = false;
                        if (allowPrivateUse
                            && TryGetObservedFallbackDiagnosticCandidate(
                                original,
                                provenance.CodePageId,
                                out string observedCandidate,
                                out string observedCandidateReason))
                        {
                            if (EnableSimplifiedChineseEngineeringPolicyRepair
                                && TryAcceptSimplifiedChineseEngineeringPolicyCandidate(
                                    original,
                                    observedCandidate,
                                    out string policyReason))
                            {
                                decoded = observedCandidate;
                                reason =
                                    $"simplified-cn-engineering-policy, Exact={reason}, " +
                                    $"ObservedCoverage={observedCandidateReason}, {policyReason}";
                                allowPrivateUse = false;
                                repairBySimplifiedPolicy = true;
                                promotedBySimplifiedPolicy = true;
                                simplifiedPolicyAcceptedCount++;
                            }
                            else
                            {
                                string policyRejectReason = EnableSimplifiedChineseEngineeringPolicyRepair
                                    ? GetSimplifiedChineseEngineeringPolicyRejectReason(original, observedCandidate)
                                    : "policy-disabled";
                                simplifiedPolicyRejectedCount++;
                                observedBlockedByNativeExactCount++;
                                if (observedBlockedLogCount++ < SkipLogLimit)
                                {
                                    DiagnosticLogger.Log(
                                        "DBText证据修复",
                                        $"阻塞 Handle={dbText.Handle}: exact-current blocks observed fallback, " +
                                        $"Exact={reason}, " +
                                        $"PolicyReject={policyRejectReason}, " +
                                        $"CodePage={DwgFilerCodePageScopeHook.FormatCodePageId(provenance.CodePageId)}, " +
                                        $"ReadStringEvents={provenance.ReadStringEvents.Length}, " +
                                        $"DwgInRaw={FormatDwgInRawSummary(provenance.DwgInRaw, provenance.CodePageId)}, " +
                                        $"ObservedCoverage={observedCandidateReason}, Current='{EscapeForLog(TrimForLog(original))}', " +
                                        $"Observed='{EscapeForLog(TrimForLog(observedCandidate))}'");
                                }
                            }
                        }

                        if (!promotedBySimplifiedPolicy)
                        {
                            skipped++;
                            continue;
                        }
                    }

                    eligiblePreview++;
                    if (!TryValidateNativeDecodedTextForRepair(decoded, out string invalidReason, allowPrivateUse))
                    {
                        skipped++;
                        invalidDecoded++;
                        if (invalidDecoded <= InvalidDecodedLogLimit)
                        {
                            DiagnosticLogger.Log(
                                "DBText证据修复",
                                $"跳过 Handle={dbText.Handle}: native decoded text rejected, {invalidReason}, " +
                                $"Original='{EscapeForLog(TrimForLog(original))}', Decoded='{EscapeForLog(TrimForLog(decoded))}'");
                        }

                        continue;
                    }

                    dbText.UpgradeOpen();
                    dbText.TextString = decoded;
                    repaired++;
                    if (repairBySimplifiedPolicy)
                        simplifiedPolicyRepairedCount++;

                    if (repaired <= CandidateLogLimit)
                    {
                        DiagnosticLogger.Log(
                            "DBText证据修复",
                            $"已修复 Handle={dbText.Handle}, ImpText=0x{impText.ToInt64():X}, " +
                            $"CodePage={DwgFilerCodePageScopeHook.FormatCodePageId(provenance.CodePageId)}, " +
                            $"Len={original.Length}->{decoded.Length}, Coverage={reason}, " +
                            $"Text='{EscapeForLog(decoded)}'");
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    DiagnosticLogger.Log("DBText证据修复",
                        $"实体处理失败 ObjectId={entityId}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        tr.Commit();
        DiagnosticLogger.Log(
            "DBText证据修复",
            $"扫描={scanned}, 可预览={eligiblePreview}, 缺少对象证据={missingProvenance}, " +
            $"exact序列={exactNativeSequenceCount}, observed诊断={observedDiagnosticOnlyCount}, " +
            $"observed阻塞={observedBlockedByNativeExactCount}, PUA符号保留={privateUseSymbolPreservedCount}, " +
            $"简体策略接受={simplifiedPolicyAcceptedCount}, 简体策略拒绝={simplifiedPolicyRejectedCount}, " +
            $"简体策略修复={simplifiedPolicyRepairedCount}, " +
            $"independentWide诊断={independentWideSourceDiagnosticCount}, " +
            $"independentCarrier诊断={independentCarrierSourceDiagnosticCount}, " +
            $"证据不一致={provenanceMismatch}, 跳过={skipped}, 无效解码={invalidDecoded}, " +
            $"错误={errors}, 实际修复={repaired}");
        return repaired;
    }

    internal static bool TryGetNativeImpTextPointer(DBText dbText, out IntPtr impText)
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

    internal static bool TryBuildTextFromNativeDbcsSequence(
        string currentText,
        string nativeDbcsDecodedText,
        out string decoded,
        out string reason)
    {
        decoded = string.Empty;
        reason = "<empty>";

        if (string.IsNullOrEmpty(currentText) || string.IsNullOrEmpty(nativeDbcsDecodedText))
            return false;

        int nonAsciiCount = 0;
        for (int i = 0; i < currentText.Length; i++)
        {
            if (currentText[i] > 0x7F)
                nonAsciiCount++;
        }

        if (nonAsciiCount == 0)
        {
            reason = "no-dbcs-carrier-chars";
            return false;
        }

        if (nativeDbcsDecodedText.Length != nonAsciiCount)
        {
            reason = $"partial native-dbcs-sequence currentNonAscii={nonAsciiCount}, nativeDbcs={nativeDbcsDecodedText.Length}";
            return false;
        }

        var builder = new StringBuilder(currentText.Length);
        int dbcsIndex = 0;
        for (int i = 0; i < currentText.Length; i++)
        {
            char ch = currentText[i];
            builder.Append(ch > 0x7F ? nativeDbcsDecodedText[dbcsIndex++] : ch);
        }

        decoded = builder.ToString();
        reason = $"native-exact dbcs={nonAsciiCount}";
        return true;
    }

    private static NativeImpTextIndependentSourceReport InspectNativeIndependentSource(
        IntPtr impText,
        string currentText,
        string observedCandidate)
    {
        TextEditorDbcsDecodeHook.TryBuildObservedCarrierBytes(
            currentText,
            out byte[] carrierBytes,
            out _);

        return NativeImpTextIndependentSourceProbe.Inspect(
            impText,
            currentText,
            observedCandidate,
            carrierBytes);
    }

    private static string FormatIndependentSourceSummary(NativeImpTextIndependentSourceReport report)
    {
        return
            $"wideCandidate={report.CandidateWideSlotCount}, " +
            $"wideIndependent={report.IndependentCandidateWideSlotCount}, " +
            $"carrierSlots={report.CandidateCarrierByteSlotCount}, " +
            $"inlineCandidate={FormatOffset(report.CandidateInlineWideOffset)}, " +
            $"decision={report.Decision}";
    }

    private static string FormatOffset(int offset)
    {
        return offset < 0 ? "-1" : $"+0x{offset:X}";
    }

    internal static bool TryGetObservedFallbackDiagnosticCandidate(
        string currentText,
        int codePageId,
        out string decoded,
        out string reason)
    {
        decoded = string.Empty;
        reason = "<empty>";

        if (!TextEditorDbcsDecodeHook.TryDecodeWithObservedEvidence(
                currentText,
                codePageId,
                out string observedDecoded,
                out string observedReason,
                allowNativeExpansion: true))
        {
            reason = observedReason;
            return false;
        }

        string candidate = observedDecoded;
        string candidateReason = "observed-fallback " + observedReason;
        if (TryNormalizePrivateUseSymbolsFromOriginal(
                currentText,
                observedDecoded,
                out string normalizedObserved,
                out string normalizeReason))
        {
            candidate = normalizedObserved;
            candidateReason += ", " + normalizeReason;
        }

        if (string.Equals(currentText, candidate, StringComparison.Ordinal))
        {
            reason = "already-exact-native-text";
            return false;
        }

        if (!TryValidateNativeDecodedTextForRepair(candidate, out string validReason))
        {
            reason = validReason;
            return false;
        }

        decoded = candidate;
        reason = candidateReason;
        return true;
    }

    internal static bool TryBuildTextFromNativeDbcsBytes(
        string currentText,
        byte[] nativeDbcsBytes,
        int codePageId,
        out string decoded,
        out string reason)
    {
        decoded = string.Empty;
        reason = "<empty>";

        if (string.IsNullOrEmpty(currentText) || nativeDbcsBytes.Length == 0)
            return false;

        int nonAsciiCount = 0;
        for (int i = 0; i < currentText.Length; i++)
        {
            if (currentText[i] > 0x7F)
                nonAsciiCount++;
        }

        if (nonAsciiCount == 0)
        {
            reason = "no-dbcs-carrier-chars";
            return false;
        }

        int expectedByteCount = nonAsciiCount * 2;
        if (nativeDbcsBytes.Length != expectedByteCount)
        {
            reason = $"partial native-dbcs-bytes currentNonAscii={nonAsciiCount}, nativeBytes={nativeDbcsBytes.Length}";
            return false;
        }

        var builder = new StringBuilder(currentText.Length);
        int byteIndex = 0;
        int nativeDecodedCount = 0;
        for (int i = 0; i < currentText.Length; i++)
        {
            char ch = currentText[i];
            if (ch <= 0x7F)
            {
                builder.Append(ch);
                continue;
            }

            byte firstByte = nativeDbcsBytes[byteIndex++];
            byte secondByte = nativeDbcsBytes[byteIndex++];
            if (!TextEditorDbcsDecodeHook.TryDecodeNativeBytePair(
                    codePageId,
                    firstByte,
                    secondByte,
                    out char mapped))
            {
                reason = $"native-byte-decode-failed at {i}, bytes={firstByte:X2} {secondByte:X2}";
                return false;
            }

            builder.Append(mapped);
            nativeDecodedCount++;
        }

        decoded = builder.ToString();
        reason = $"native-raw-bytes dbcs={nativeDecodedCount}";
        return true;
    }

    internal static bool TryBuildTextFromTerminalDwgField(
        string currentText,
        NativeDwgInRawSnapshot snapshot,
        int codePageId,
        out string decoded,
        out string reason)
    {
        decoded = string.Empty;
        reason = "<empty>";

        if (string.IsNullOrEmpty(currentText))
            return false;

        if (!TryExtractTerminalDwgTextField(
                snapshot,
                out byte[] terminalBytes,
                out int lengthMarkerOffset,
                out int prefixByte,
                out string extractReason))
        {
            reason = extractReason;
            return false;
        }

        if (!TryDecodeDwgTextBytesWithCodePage(
                terminalBytes,
                codePageId,
                out string terminalText,
                out int windowsCodePage,
                out string decodeReason))
        {
            reason = decodeReason;
            return false;
        }

        if (!string.Equals(currentText, terminalText, StringComparison.Ordinal))
        {
            reason =
                $"terminal-text-mismatch offset={lengthMarkerOffset}, prefix={FormatOptionalByte(prefixByte)}, " +
                $"cp{windowsCodePage}, terminal='{EscapeForLog(TrimForLog(terminalText))}'";
            return false;
        }

        decoded = terminalText;
        reason =
            $"terminal-exact offset={lengthMarkerOffset}, prefix={FormatOptionalByte(prefixByte)}, " +
            $"bytes={FormatBytes(terminalBytes, 16)}, cp{windowsCodePage}";
        return true;
    }

    internal static bool TryExtractTerminalDwgTextField(
        NativeDwgInRawSnapshot snapshot,
        out byte[] textBytes,
        out int lengthMarkerOffset,
        out int prefixByte,
        out string reason)
    {
        textBytes = [];
        lengthMarkerOffset = -1;
        prefixByte = -1;
        reason = "<empty>";

        byte[] raw = snapshot.RawBytes;
        if (snapshot.IsTruncated)
        {
            reason = "raw-snapshot-truncated";
            return false;
        }

        if (raw.Length < 3)
        {
            reason = $"raw-too-short len={raw.Length}";
            return false;
        }

        if (raw[^1] != 0)
        {
            reason = "terminal-null-not-found";
            return false;
        }

        for (int offset = raw.Length - 2; offset >= 0; offset--)
        {
            int declaredLength = raw[offset];
            if (declaredLength <= 1)
                continue;

            if (offset + 1 + declaredLength != raw.Length)
                continue;

            int payloadLength = declaredLength - 1;
            textBytes = new byte[payloadLength];
            Array.Copy(raw, offset + 1, textBytes, 0, payloadLength);
            lengthMarkerOffset = offset;
            prefixByte = offset > 0 ? raw[offset - 1] : -1;
            reason = $"terminal-length-field offset={offset}, declared={declaredLength}, payload={payloadLength}";
            return true;
        }

        reason = "terminal-length-field-not-found";
        return false;
    }

    internal static bool TryDecodeDwgTextBytesWithCodePage(
        byte[] bytes,
        int codePageId,
        out string decoded,
        out int windowsCodePage,
        out string reason)
    {
        decoded = string.Empty;
        windowsCodePage = 0;
        reason = "<empty>";

        if (!TryMapAutoCadCodePageIdToWindowsCodePage(codePageId, out windowsCodePage))
        {
            reason = $"unsupported-code-page-id 0x{codePageId:X}";
            return false;
        }

        return TryDecodeDwgTextBytesWithWindowsCodePage(bytes, windowsCodePage, out decoded, out reason);
    }

    internal static bool TryEncodeDwgTextWithCodePage(
        string text,
        int codePageId,
        out byte[] bytes,
        out int windowsCodePage,
        out string reason)
    {
        bytes = [];
        windowsCodePage = 0;
        reason = "<empty>";

        if (string.IsNullOrEmpty(text))
        {
            reason = "empty-text";
            return false;
        }

        if (!TryMapAutoCadCodePageIdToWindowsCodePage(codePageId, out windowsCodePage))
        {
            reason = $"unsupported-code-page-id 0x{codePageId:X}";
            return false;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding = Encoding.GetEncoding(
                windowsCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            bytes = encoding.GetBytes(text);
            reason = $"cp{windowsCodePage}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"cp{windowsCodePage} encode-failed {ex.GetType().Name}";
            return false;
        }
    }

    internal static bool TryDecodeDwgTextBytesWithWindowsCodePage(
        byte[] bytes,
        int windowsCodePage,
        out string decoded,
        out string reason)
    {
        decoded = string.Empty;
        reason = "<empty>";

        if (bytes.Length == 0)
        {
            reason = "empty-bytes";
            return false;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding = Encoding.GetEncoding(
                windowsCodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            decoded = encoding.GetString(bytes);
            reason = $"cp{windowsCodePage}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"cp{windowsCodePage} decode-failed {ex.GetType().Name}";
            return false;
        }
    }

    internal static bool TryMapAutoCadCodePageIdToWindowsCodePage(int codePageId, out int windowsCodePage)
    {
        windowsCodePage = codePageId switch
        {
            0x27 => 936,
            0x28 => 950,
            _ => 0
        };
        return windowsCodePage != 0;
    }

    internal static bool TryGetAlternateDbcsWindowsCodePage(int windowsCodePage, out int alternateWindowsCodePage)
    {
        alternateWindowsCodePage = windowsCodePage switch
        {
            936 => 950,
            950 => 936,
            _ => 0
        };
        return alternateWindowsCodePage != 0;
    }

    internal static bool TryValidateNativeDecodedTextForRepair(
        string text,
        out string reason,
        bool allowPrivateUse = false)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsSurrogate(ch))
            {
                reason = $"surrogate U+{(int)ch:X4} at {i}";
                return false;
            }

            if (ch == '\uFFFD')
            {
                reason = $"replacement-char at {i}";
                return false;
            }

            if (!allowPrivateUse && ch >= '\uE000' && ch <= '\uF8FF')
            {
                reason = $"private-use U+{(int)ch:X4} at {i}";
                return false;
            }

            if (char.IsControl(ch) && ch != '\t')
            {
                reason = $"control U+{(int)ch:X4} at {i}";
                return false;
            }
        }

        reason = "ok";
        return true;
    }

    internal static bool TryNormalizePrivateUseSymbolsFromOriginal(
        string original,
        string decoded,
        out string normalized,
        out string reason)
    {
        normalized = decoded;
        reason = "no-private-use";

        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(decoded))
            return false;

        if (!ContainsPrivateUse(decoded))
            return false;

        if (original.Length != decoded.Length)
        {
            reason = $"private-use length mismatch original={original.Length}, decoded={decoded.Length}";
            return false;
        }

        var builder = new StringBuilder(decoded.Length);
        int preserved = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            char decodedChar = decoded[i];
            if (!IsPrivateUse(decodedChar))
            {
                builder.Append(decodedChar);
                continue;
            }

            char originalChar = original[i];
            if (!IsSymbolOrPunctuation(originalChar))
            {
                reason = $"private-use U+{(int)decodedChar:X4} at {i} cannot preserve original U+{(int)originalChar:X4}";
                return false;
            }

            builder.Append(originalChar);
            preserved++;
        }

        normalized = builder.ToString();
        reason = $"private-use-symbol-preserved count={preserved}";
        return preserved > 0;
    }

    private static bool ContainsPrivateUse(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (IsPrivateUse(text[i]))
                return true;
        }

        return false;
    }

    private static bool IsPrivateUse(char ch)
    {
        return ch >= '\uE000' && ch <= '\uF8FF';
    }

    private static bool IsSymbolOrPunctuation(char ch)
    {
        if (char.IsLetterOrDigit(ch))
            return false;

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category == UnicodeCategory.ConnectorPunctuation
            || category == UnicodeCategory.DashPunctuation
            || category == UnicodeCategory.OpenPunctuation
            || category == UnicodeCategory.ClosePunctuation
            || category == UnicodeCategory.InitialQuotePunctuation
            || category == UnicodeCategory.FinalQuotePunctuation
            || category == UnicodeCategory.OtherPunctuation
            || category == UnicodeCategory.MathSymbol
            || category == UnicodeCategory.CurrencySymbol
            || category == UnicodeCategory.ModifierSymbol
            || category == UnicodeCategory.OtherSymbol;
    }

    internal static bool TryAcceptSimplifiedChineseEngineeringPolicyCandidate(
        string currentText,
        string candidate,
        out string reason)
    {
        reason = GetSimplifiedChineseEngineeringPolicyRejectReason(currentText, candidate);
        return reason.StartsWith("accepted ", StringComparison.Ordinal);
    }

    private static string GetSimplifiedChineseEngineeringPolicyRejectReason(
        string currentText,
        string candidate)
    {
        if (string.IsNullOrEmpty(currentText) || string.IsNullOrEmpty(candidate))
            return "empty-current-or-candidate";

        if (string.Equals(currentText, candidate, StringComparison.Ordinal))
            return "same-text";

        string currentAscii = ExtractAsciiSkeleton(currentText);
        string candidateAscii = ExtractAsciiSkeleton(candidate);
        if (!string.Equals(currentAscii, candidateAscii, StringComparison.Ordinal))
        {
            return
                $"ascii-skeleton-mismatch current='{EscapeForLog(TrimForLog(currentAscii))}', " +
                $"candidate='{EscapeForLog(TrimForLog(candidateAscii))}'";
        }

        AnalyzeSimplifiedChineseEngineeringText(
            currentText,
            out int currentDisallowed,
            out int currentSimplifiedCjk,
            out _,
            out string currentFirstDisallowed);
        AnalyzeSimplifiedChineseEngineeringText(
            candidate,
            out int candidateDisallowed,
            out int candidateSimplifiedCjk,
            out int candidatePrivateUse,
            out string candidateFirstDisallowed);

        if (currentDisallowed == 0)
        {
            return
                $"current-already-valid-simplified-english cjk={currentSimplifiedCjk}, " +
                "english-ascii-preserved";
        }

        if (candidateDisallowed != 0)
        {
            return
                $"candidate-not-valid-simplified-english disallowed={candidateDisallowed}, " +
                $"first={candidateFirstDisallowed}";
        }

        if (candidatePrivateUse != 0)
            return $"candidate-private-use count={candidatePrivateUse}";

        if (candidateSimplifiedCjk == 0)
            return "candidate-has-no-simplified-cjk";

        return
            $"accepted domain=simplified-cn-engineering, english-ascii-stable, " +
            $"currentDisallowed={currentDisallowed}, currentFirst={currentFirstDisallowed}, " +
            $"candidateSimplifiedCjk={candidateSimplifiedCjk}";
    }

    private static void AnalyzeSimplifiedChineseEngineeringText(
        string text,
        out int disallowedCount,
        out int simplifiedCjkCount,
        out int privateUseCount,
        out string firstDisallowed)
    {
        disallowedCount = 0;
        simplifiedCjkCount = 0;
        privateUseCount = 0;
        firstDisallowed = "<none>";

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (IsPrivateUse(ch))
                privateUseCount++;

            if (IsAllowedSimplifiedChineseEngineeringChar(ch, out bool isSimplifiedCjk, out string rejectReason))
            {
                if (isSimplifiedCjk)
                    simplifiedCjkCount++;
                continue;
            }

            disallowedCount++;
            if (firstDisallowed == "<none>")
                firstDisallowed = $"U+{(int)ch:X4}@{i}:{rejectReason}";
        }
    }

    private static bool IsAllowedSimplifiedChineseEngineeringChar(
        char ch,
        out bool isSimplifiedCjk,
        out string rejectReason)
    {
        isSimplifiedCjk = false;
        rejectReason = "ok";

        if (ch <= 0x7F)
            return !char.IsControl(ch) || ch == '\t';

        if (char.IsWhiteSpace(ch))
            return true;

        if (IsPrivateUse(ch))
        {
            rejectReason = "private-use";
            return false;
        }

        if (IsBopomofo(ch))
        {
            rejectReason = "bopomofo";
            return false;
        }

        if (IsKana(ch) || IsHangul(ch))
        {
            rejectReason = "non-chinese-script";
            return false;
        }

        if (IsCjkCompatibilityIdeograph(ch))
        {
            rejectReason = "cjk-compatibility-ideograph";
            return false;
        }

        if (IsCjkUnifiedIdeograph(ch))
        {
            if (Gb2312CommonCharSet.Value.Contains(ch))
            {
                isSimplifiedCjk = true;
                return true;
            }

            rejectReason = "not-in-gb2312-common-simplified-set";
            return false;
        }

        if (IsFullwidthAscii(ch) || IsAllowedEngineeringSymbolOrPunctuation(ch))
            return true;

        rejectReason = "unsupported-symbol-or-script";
        return false;
    }

    private static HashSet<char> BuildGb2312CommonCharSet()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(936);
        var chars = new HashSet<char>();

        for (int high = 0xB0; high <= 0xF7; high++)
        {
            for (int low = 0xA1; low <= 0xFE; low++)
            {
                string decoded = encoding.GetString([(byte)high, (byte)low]);
                if (decoded.Length == 1 && IsCjkUnifiedIdeograph(decoded[0]))
                    chars.Add(decoded[0]);
            }
        }

        return chars;
    }

    private static string ExtractAsciiSkeleton(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x7F)
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool IsCjkUnifiedIdeograph(char ch)
    {
        return (ch >= '\u3400' && ch <= '\u4DBF')
            || (ch >= '\u4E00' && ch <= '\u9FFF');
    }

    private static bool IsCjkCompatibilityIdeograph(char ch)
    {
        return ch >= '\uF900' && ch <= '\uFAFF';
    }

    private static bool IsBopomofo(char ch)
    {
        return ch >= '\u3100' && ch <= '\u312F';
    }

    private static bool IsKana(char ch)
    {
        return (ch >= '\u3040' && ch <= '\u30FF')
            || (ch >= '\u31F0' && ch <= '\u31FF');
    }

    private static bool IsHangul(char ch)
    {
        return ch >= '\uAC00' && ch <= '\uD7AF';
    }

    private static bool IsFullwidthAscii(char ch)
    {
        return ch >= '\uFF01' && ch <= '\uFF5E';
    }

    private static bool IsAllowedEngineeringSymbolOrPunctuation(char ch)
    {
        const string allowed =
            "　、。，．·；：？！“”‘’（）《》〈〉【】〔〕「」『』—－…～￥" +
            "℃㎡㎜㎝㎞㎏μΩΦφ×÷±°‰′″≤≥≈≠∠∅Ø";
        if (allowed.IndexOf(ch) >= 0)
            return true;

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category == UnicodeCategory.DecimalDigitNumber
            || category == UnicodeCategory.MathSymbol
            || category == UnicodeCategory.CurrencySymbol;
    }

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static string FormatDwgInRawSummary(NativeDwgInRawSnapshot snapshot, int codePageId)
    {
        string range = snapshot.StartByteOffset < 0 || snapshot.StartBitOffset < 0
            ? "<none>"
            : $"{snapshot.StartByteOffset}:{snapshot.StartBitOffset}->{snapshot.EndByteOffset}:{snapshot.EndBitOffset}";
        string summary = $"{range}, raw={FormatBytes(snapshot.RawBytes, 24)}{(snapshot.IsTruncated ? " ..." : string.Empty)}";
        if (TryExtractTerminalDwgTextField(
                snapshot,
                out byte[] terminalBytes,
                out int lengthOffset,
                out int prefixByte,
                out _))
        {
            summary +=
                $", terminal=offset:{lengthOffset},prefix:{FormatOptionalByte(prefixByte)}," +
                $"bytes:{FormatBytes(terminalBytes, 16)}";
            if (TryDecodeDwgTextBytesWithCodePage(
                    terminalBytes,
                    codePageId,
                    out string terminalText,
                    out int windowsCodePage,
                    out _))
            {
                summary += $", terminalCp{windowsCodePage}='{EscapeForLog(TrimForLog(terminalText))}'";
            }
        }

        return summary;
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

    private static string FormatOptionalByte(int value)
    {
        return value < 0 ? "<none>" : value.ToString("X2");
    }

    private static string TrimForLog(string text)
    {
        if (text.Length <= MaxLoggedTextChars)
            return text;

        return text[..MaxLoggedTextChars] + "...";
    }
}
#endif
