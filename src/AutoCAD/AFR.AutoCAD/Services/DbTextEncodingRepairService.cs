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
    private const int ProfileRepeatThreshold = 3;
    private const string ExplicitAllowedEngineeringSymbols =
        "　、。，．·；：？！“”‘’（）《》〈〉【】〔〕「」『』—－…～￥" +
        "¨〃ⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩⅪⅫ℃㎡㎜㎝㎞㎏μΩΦφ×÷±°‰′″≤≥≈≠∠∅Ø";
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
        int manualLabelAcceptedCount = 0;
        int manualLabelBlockedCount = 0;
        int manualLabelRepairedCount = 0;
        int simplifiedPolicyAcceptedCount = 0;
        int simplifiedPolicyRejectedCount = 0;
        int simplifiedPolicyRepairedCount = 0;
        int repaired = 0;

        DbTextAiTrainingSampleService.BeginSession(db);
        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        ObservedFallbackRepairProfile observedProfile = BuildObservedFallbackRepairProfile(tr, blockTable);
        DbTextManualLabelIndex manualLabelIndex = DbTextManualLabelService.LoadIndex(db);
        if (manualLabelIndex.Count > 0)
        {
            DiagnosticLogger.Log(
                "DBText证据修复",
                $"已加载人工标签: Count={manualLabelIndex.Count}, DrawingSha256={manualLabelIndex.DrawingSha256}");
        }

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
                    bool repairByManualLabel = false;
                    bool repairBySimplifiedPolicy = false;
                    DbTextPolicyContext policyContext = GetPolicyContext(tr, dbText);
                    string decoded;
                    string reason;
                    if (manualLabelIndex.TryFindCurrent(
                            dbText,
                            original,
                            out DbTextManualLabelRecord currentManualLabel))
                    {
                        if (string.Equals(currentManualLabel.Action, DbTextManualLabelService.ActionRepair, StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(currentManualLabel.SelectedText)
                            && !string.Equals(original, currentManualLabel.SelectedText, StringComparison.Ordinal))
                        {
                            decoded = currentManualLabel.SelectedText;
                            reason = $"manual-label-direct, LabelUtc={currentManualLabel.TimestampUtc}";
                            allowPrivateUse = true;
                            repairByManualLabel = true;
                            manualLabelAcceptedCount++;
                        }
                        else
                        {
                            manualLabelBlockedCount++;
                            skipped++;
                            if (observedDiagnosticLogCount++ < SkipLogLimit)
                            {
                                DiagnosticLogger.Log(
                                    "DBText证据修复",
                                    $"人工标签阻塞 Handle={dbText.Handle}: direct-current, Action={currentManualLabel.Action}, " +
                                    $"Current='{EscapeForLog(TrimForLog(original))}', " +
                                    $"Selected='{EscapeForLog(TrimForLog(currentManualLabel.SelectedText))}'");
                            }

                            continue;
                        }
                    }
                    else if (TryBuildTextFromNativeDbcsSequence(
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

                            if (manualLabelIndex.TryFind(
                                    dbText,
                                    original,
                                    observedCandidate,
                                    out DbTextManualLabelRecord manualLabel))
                            {
                                if (string.Equals(manualLabel.Action, DbTextManualLabelService.ActionRepair, StringComparison.Ordinal)
                                    && !string.IsNullOrEmpty(manualLabel.SelectedText))
                                {
                                    decoded = manualLabel.SelectedText;
                                    reason =
                                        $"manual-label, NativeEvidence={nativeReason}, " +
                                        $"ObservedCoverage={observedCandidateReason}, LabelUtc={manualLabel.TimestampUtc}";
                                    allowPrivateUse = true;
                                    repairByManualLabel = true;
                                    manualLabelAcceptedCount++;
                                    DbTextAiTrainingSampleService.RecordCandidate(
                                        db,
                                        tr,
                                        dbText,
                                        provenance,
                                        observedCandidate,
                                        observedCandidateReason,
                                        nativeReason,
                                        "repair",
                                        "manual-label");
                                }
                                else
                                {
                                    manualLabelBlockedCount++;
                                    DbTextAiTrainingSampleService.RecordCandidate(
                                        db,
                                        tr,
                                        dbText,
                                        provenance,
                                        observedCandidate,
                                        observedCandidateReason,
                                        nativeReason,
                                        "abstain",
                                        $"manual-label-action={manualLabel.Action}");
                                    observedDiagnosticOnlyCount++;
                                    if (observedDiagnosticLogCount++ < SkipLogLimit)
                                    {
                                        DiagnosticLogger.Log(
                                            "DBText证据修复",
                                            $"人工标签阻塞 Handle={dbText.Handle}: Action={manualLabel.Action}, " +
                                            $"Current='{EscapeForLog(TrimForLog(original))}', " +
                                            $"Observed='{EscapeForLog(TrimForLog(observedCandidate))}'");
                                    }
                                }
                            }
                            else if (EnableSimplifiedChineseEngineeringPolicyRepair
                                && TryAcceptSimplifiedChineseEngineeringPolicyCandidate(
                                    original,
                                    observedCandidate,
                                    policyContext,
                                    observedProfile,
                                    out string policyReason))
                            {
                                decoded = observedCandidate;
                                reason =
                                    $"simplified-cn-engineering-policy, NativeEvidence={nativeReason}, " +
                                    $"ObservedCoverage={observedCandidateReason}, {policyReason}";
                                allowPrivateUse = false;
                                repairBySimplifiedPolicy = true;
                                simplifiedPolicyAcceptedCount++;
                                DbTextAiTrainingSampleService.RecordCandidate(
                                    db,
                                    tr,
                                    dbText,
                                    provenance,
                                    observedCandidate,
                                    observedCandidateReason,
                                    nativeReason,
                                    "repair",
                                    policyReason);
                            }
                            else
                            {
                                string policyRejectReason = EnableSimplifiedChineseEngineeringPolicyRepair
                                    ? GetSimplifiedChineseEngineeringPolicyRejectReason(
                                        original,
                                        observedCandidate,
                                        policyContext,
                                        observedProfile)
                                    : "policy-disabled";
                                simplifiedPolicyRejectedCount++;
                                DbTextAiTrainingSampleService.RecordCandidate(
                                    db,
                                    tr,
                                    dbText,
                                    provenance,
                                    observedCandidate,
                                    observedCandidateReason,
                                    nativeReason,
                                    "abstain",
                                    policyRejectReason);
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

                        if (!repairByManualLabel && !repairBySimplifiedPolicy)
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
                        bool promotedByManualLabel = false;
                        bool promotedBySimplifiedPolicy = false;
                        if (allowPrivateUse
                            && TryGetObservedFallbackDiagnosticCandidate(
                                original,
                                provenance.CodePageId,
                                out string observedCandidate,
                                out string observedCandidateReason))
                        {
                            if (manualLabelIndex.TryFind(
                                    dbText,
                                    original,
                                    observedCandidate,
                                    out DbTextManualLabelRecord manualLabel))
                            {
                                if (string.Equals(manualLabel.Action, DbTextManualLabelService.ActionRepair, StringComparison.Ordinal)
                                    && !string.IsNullOrEmpty(manualLabel.SelectedText))
                                {
                                    string exactReason = reason;
                                    decoded = manualLabel.SelectedText;
                                    reason =
                                        $"manual-label, Exact={exactReason}, " +
                                        $"ObservedCoverage={observedCandidateReason}, LabelUtc={manualLabel.TimestampUtc}";
                                    allowPrivateUse = true;
                                    repairByManualLabel = true;
                                    promotedByManualLabel = true;
                                    manualLabelAcceptedCount++;
                                    DbTextAiTrainingSampleService.RecordCandidate(
                                        db,
                                        tr,
                                        dbText,
                                        provenance,
                                        observedCandidate,
                                        observedCandidateReason,
                                        exactReason,
                                        "repair",
                                        "manual-label");
                                }
                                else
                                {
                                    manualLabelBlockedCount++;
                                    DbTextAiTrainingSampleService.RecordCandidate(
                                        db,
                                        tr,
                                        dbText,
                                        provenance,
                                        observedCandidate,
                                        observedCandidateReason,
                                        reason,
                                        "abstain",
                                        $"manual-label-action={manualLabel.Action}");
                                    observedBlockedByNativeExactCount++;
                                    if (observedBlockedLogCount++ < SkipLogLimit)
                                    {
                                        DiagnosticLogger.Log(
                                            "DBText证据修复",
                                            $"人工标签阻塞 Handle={dbText.Handle}: exact-current, Action={manualLabel.Action}, " +
                                            $"Current='{EscapeForLog(TrimForLog(original))}', " +
                                            $"Observed='{EscapeForLog(TrimForLog(observedCandidate))}'");
                                    }
                                }
                            }
                            else if (EnableSimplifiedChineseEngineeringPolicyRepair
                                && TryAcceptSimplifiedChineseEngineeringPolicyCandidate(
                                    original,
                                    observedCandidate,
                                    policyContext,
                                    observedProfile,
                                    out string policyReason))
                            {
                                string exactReason = reason;
                                decoded = observedCandidate;
                                reason =
                                    $"simplified-cn-engineering-policy, Exact={exactReason}, " +
                                    $"ObservedCoverage={observedCandidateReason}, {policyReason}";
                                allowPrivateUse = false;
                                repairBySimplifiedPolicy = true;
                                promotedBySimplifiedPolicy = true;
                                simplifiedPolicyAcceptedCount++;
                                DbTextAiTrainingSampleService.RecordCandidate(
                                    db,
                                    tr,
                                    dbText,
                                    provenance,
                                    observedCandidate,
                                    observedCandidateReason,
                                    exactReason,
                                    "repair",
                                    policyReason);
                            }
                            else
                            {
                                string policyRejectReason = EnableSimplifiedChineseEngineeringPolicyRepair
                                    ? GetSimplifiedChineseEngineeringPolicyRejectReason(
                                        original,
                                        observedCandidate,
                                        policyContext,
                                        observedProfile)
                                    : "policy-disabled";
                                simplifiedPolicyRejectedCount++;
                                DbTextAiTrainingSampleService.RecordCandidate(
                                    db,
                                    tr,
                                    dbText,
                                    provenance,
                                    observedCandidate,
                                    observedCandidateReason,
                                    reason,
                                    "abstain",
                                    policyRejectReason);
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

                        if (!promotedByManualLabel && !promotedBySimplifiedPolicy)
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
                    if (repairByManualLabel)
                        manualLabelRepairedCount++;
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
            $"人工标签接受={manualLabelAcceptedCount}, 人工标签阻塞={manualLabelBlockedCount}, " +
            $"人工标签修复={manualLabelRepairedCount}, " +
            $"简体策略接受={simplifiedPolicyAcceptedCount}, 简体策略拒绝={simplifiedPolicyRejectedCount}, " +
            $"简体策略修复={simplifiedPolicyRepairedCount}, " +
            $"independentWide诊断={independentWideSourceDiagnosticCount}, " +
            $"independentCarrier诊断={independentCarrierSourceDiagnosticCount}, " +
            $"证据不一致={provenanceMismatch}, 跳过={skipped}, 无效解码={invalidDecoded}, " +
            $"错误={errors}, 实际修复={repaired}");
        DbTextAiTrainingSampleService.EndSession();
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

    private static ObservedFallbackRepairProfile BuildObservedFallbackRepairProfile(
        Transaction tr,
        BlockTable blockTable)
    {
        var profile = new ObservedFallbackRepairProfile();

        foreach (ObjectId blockId in blockTable)
        {
            BlockTableRecord block;
            try
            {
                block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                if (block.IsFromExternalReference || block.IsDependent)
                    continue;
            }
            catch
            {
                continue;
            }

            foreach (ObjectId entityId in block)
            {
                try
                {
                    if (tr.GetObject(entityId, OpenMode.ForRead, false, true) is not DBText dbText)
                        continue;

                    string current = dbText.TextString ?? string.Empty;
                    if (string.IsNullOrEmpty(current))
                        continue;

                    if (!TryGetNativeImpTextPointer(dbText, out IntPtr impText)
                        || !DbTextDwgInFieldsScopeHook.TryGetProvenance(impText, out NativeDbTextProvenance provenance))
                    {
                        continue;
                    }

                    if (!string.Equals(provenance.NativeText, current, StringComparison.Ordinal))
                        continue;

                    if (!TryGetObservedFallbackDiagnosticCandidate(
                            current,
                            provenance.CodePageId,
                            out string candidate,
                            out _))
                    {
                        continue;
                    }

                    profile.Add(current, candidate, GetPolicyContext(tr, dbText));
                }
                catch
                {
                    // Profile building is advisory; individual failures must not affect repair.
                }
            }
        }

        return profile;
    }

    private static DbTextPolicyContext GetPolicyContext(Transaction tr, DBText dbText)
    {
        string textStyleName = string.Empty;
        try
        {
            if (tr.GetObject(dbText.TextStyleId, OpenMode.ForRead, false, true) is TextStyleTableRecord style)
                textStyleName = style.Name;
        }
        catch
        {
            textStyleName = string.Empty;
        }

        string ownerBlockName = string.Empty;
        try
        {
            if (tr.GetObject(dbText.OwnerId, OpenMode.ForRead, false, true) is BlockTableRecord owner)
                ownerBlockName = owner.Name;
        }
        catch
        {
            ownerBlockName = string.Empty;
        }

        string layer = string.Empty;
        try
        {
            layer = dbText.Layer ?? string.Empty;
        }
        catch
        {
            layer = string.Empty;
        }

        return new DbTextPolicyContext(layer, ownerBlockName, textStyleName);
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
        return TryAcceptSimplifiedChineseEngineeringPolicyCandidate(
            currentText,
            candidate,
            DbTextPolicyContext.Empty,
            ObservedFallbackRepairProfile.Empty,
            out reason);
    }

    private static bool TryAcceptSimplifiedChineseEngineeringPolicyCandidate(
        string currentText,
        string candidate,
        DbTextPolicyContext context,
        ObservedFallbackRepairProfile profile,
        out string reason)
    {
        reason = GetSimplifiedChineseEngineeringPolicyRejectReason(currentText, candidate, context, profile);
        return reason.StartsWith("accepted ", StringComparison.Ordinal);
    }

    private static string GetSimplifiedChineseEngineeringPolicyRejectReason(
        string currentText,
        string candidate)
    {
        return GetSimplifiedChineseEngineeringPolicyRejectReason(
            currentText,
            candidate,
            DbTextPolicyContext.Empty,
            ObservedFallbackRepairProfile.Empty);
    }

    private static string GetSimplifiedChineseEngineeringPolicyRejectReason(
        string currentText,
        string candidate,
        DbTextPolicyContext context,
        ObservedFallbackRepairProfile profile)
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
            if (TryAcceptEngineeringSymbolOnlyCandidate(currentText, candidate, out string symbolOnlyReason))
            {
                return
                    $"accepted domain=simplified-cn-engineering-symbol-only, english-ascii-stable, " +
                    "current-valid-known-garbled-symbol, " + symbolOnlyReason;
            }

            if (profile.TryAcceptCurrentValidCandidate(currentText, candidate, context, out string profileReason))
            {
                return
                    $"accepted domain=simplified-cn-engineering-profile, english-ascii-stable, " +
                    profileReason;
            }

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
        {
            if (TryAcceptEngineeringSymbolOnlyCandidate(currentText, candidate, out string symbolOnlyReason))
            {
                return
                    $"accepted domain=simplified-cn-engineering-symbol-only, english-ascii-stable, " +
                    $"currentDisallowed={currentDisallowed}, currentFirst={currentFirstDisallowed}, " +
                    symbolOnlyReason;
            }

            return "candidate-has-no-simplified-cjk";
        }

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
        if (IsExplicitAllowedEngineeringSymbol(ch))
            return true;

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category == UnicodeCategory.DecimalDigitNumber
            || category == UnicodeCategory.MathSymbol
            || category == UnicodeCategory.CurrencySymbol;
    }

    private static bool TryAcceptEngineeringSymbolOnlyCandidate(
        string currentText,
        string candidate,
        out string reason)
    {
        reason = "not-symbol-only";

        if (ContainsCjkUnifiedIdeograph(currentText) || ContainsCjkUnifiedIdeograph(candidate))
            return false;

        if (!ContainsNonAsciiSymbolOrPunctuation(candidate))
            return false;

        if (!ContainsBopomofoOrKnownGarbledPunctuation(currentText))
            return false;

        reason = "candidateSymbolOnly=true";
        return true;
    }

    private static bool ContainsCjkUnifiedIdeograph(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (IsCjkUnifiedIdeograph(text[i]))
                return true;
        }

        return false;
    }

    private static bool ContainsNonAsciiSymbolOrPunctuation(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch > 0x7F && IsExplicitAllowedEngineeringSymbol(ch))
                return true;
        }

        return false;
    }

    private static bool ContainsBopomofoOrKnownGarbledPunctuation(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (IsBopomofo(ch)
                || ch == '\u02D9'
                || ch == '\u22A5'
                || ch == '\uFE5D'
                || ch == '\uFE5C'
                || ch == '\u2252'
                || ch == '\u2251'
                || ch == '\u2261')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitAllowedEngineeringSymbol(char ch)
    {
        return ExplicitAllowedEngineeringSymbols.IndexOf(ch) >= 0;
    }

    private static bool CanUseCurrentValidProfileCandidate(string currentText, string candidate)
    {
        if (string.IsNullOrEmpty(currentText) || string.IsNullOrEmpty(candidate))
            return false;

        if (string.Equals(currentText, candidate, StringComparison.Ordinal))
            return false;

        if (!string.Equals(ExtractAsciiSkeleton(currentText), ExtractAsciiSkeleton(candidate), StringComparison.Ordinal))
            return false;

        AnalyzeSimplifiedChineseEngineeringText(
            currentText,
            out int currentDisallowed,
            out _,
            out _,
            out _);
        if (currentDisallowed != 0)
            return false;

        AnalyzeSimplifiedChineseEngineeringText(
            candidate,
            out int candidateDisallowed,
            out int candidateSimplifiedCjk,
            out int candidatePrivateUse,
            out _);
        if (candidateDisallowed != 0 || candidatePrivateUse != 0 || candidateSimplifiedCjk == 0)
            return false;

        return ContainsOnlyProfileSafeCandidateChars(candidate);
    }

    private static bool ContainsOnlyProfileSafeCandidateChars(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x7F || char.IsWhiteSpace(ch))
                continue;

            if (IsCjkUnifiedIdeograph(ch) && Gb2312CommonCharSet.Value.Contains(ch))
                continue;

            if (IsFullwidthAscii(ch) || IsExplicitAllowedEngineeringSymbol(ch))
                continue;

            return false;
        }

        return true;
    }

    private static string NormalizeForShortTermProfile(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == ' ' || ch == '\t')
                continue;

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private readonly record struct DbTextPolicyContext(
        string Layer,
        string OwnerBlockName,
        string TextStyleName)
    {
        public static DbTextPolicyContext Empty { get; } = new(string.Empty, string.Empty, string.Empty);
    }

    private sealed class ObservedFallbackRepairProfile
    {
        public static ObservedFallbackRepairProfile Empty { get; } = new();

        private readonly Dictionary<string, int> _mappingCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _candidatesByCurrentLayer = new(StringComparer.Ordinal);

        public void Add(string currentText, string candidate, DbTextPolicyContext context)
        {
            if (!CanUseCurrentValidProfileCandidate(currentText, candidate))
                return;

            string key = MakeMappingKey(currentText, candidate, context.Layer);
            _mappingCounts.TryGetValue(key, out int count);
            _mappingCounts[key] = count + 1;

            string currentLayerKey = MakeCurrentLayerKey(currentText, context.Layer);
            if (!_candidatesByCurrentLayer.TryGetValue(currentLayerKey, out HashSet<string>? candidates))
            {
                candidates = new HashSet<string>(StringComparer.Ordinal);
                _candidatesByCurrentLayer[currentLayerKey] = candidates;
            }

            candidates.Add(candidate);
        }

        public bool TryAcceptCurrentValidCandidate(
            string currentText,
            string candidate,
            DbTextPolicyContext context,
            out string reason)
        {
            reason = "profile-not-applicable";
            if (!CanUseCurrentValidProfileCandidate(currentText, candidate))
                return false;

            string currentLayerKey = MakeCurrentLayerKey(currentText, context.Layer);
            if (_candidatesByCurrentLayer.TryGetValue(currentLayerKey, out HashSet<string>? candidates)
                && candidates.Count != 1)
            {
                reason = $"profile-conflicting-candidates count={candidates.Count}";
                return false;
            }

            string mappingKey = MakeMappingKey(currentText, candidate, context.Layer);
            _mappingCounts.TryGetValue(mappingKey, out int count);
            if (count >= ProfileRepeatThreshold)
            {
                reason =
                    $"profile-repeat count={count}, threshold={ProfileRepeatThreshold}, " +
                    $"layer='{EscapeForLog(context.Layer)}', style='{EscapeForLog(context.TextStyleName)}'";
                return true;
            }

            if (TryAcceptKnownContextTerm(currentText, candidate, context, out string contextReason))
            {
                reason = contextReason;
                return true;
            }

            reason = $"profile-repeat-too-low count={count}, threshold={ProfileRepeatThreshold}";
            return false;
        }

        private static bool TryAcceptKnownContextTerm(
            string currentText,
            string candidate,
            DbTextPolicyContext context,
            out string reason)
        {
            reason = "known-context-not-matched";
            string normalizedCandidate = NormalizeForShortTermProfile(candidate);
            string normalizedCurrent = NormalizeForShortTermProfile(currentText);

            if (string.Equals(context.Layer, "图框", StringComparison.Ordinal)
                && string.Equals(context.TextStyleName, "TKZ", StringComparison.Ordinal)
                && (normalizedCandidate == "审定"
                    || normalizedCandidate == "审核"
                    || normalizedCandidate == "版"))
            {
                reason =
                    $"known-title-block-term term='{EscapeForLog(normalizedCandidate)}', " +
                    $"current='{EscapeForLog(normalizedCurrent)}'";
                return true;
            }

            if ((string.Equals(context.TextStyleName, "KHZ", StringComparison.Ordinal)
                    || string.Equals(context.Layer, "0", StringComparison.Ordinal))
                && normalizedCandidate == "材料表")
            {
                reason =
                    $"known-material-table-term current='{EscapeForLog(normalizedCurrent)}'";
                return true;
            }

            if (candidate.Contains("GB", StringComparison.Ordinal)
                && candidate.Contains('版')
                && currentText.Contains('唳'))
            {
                reason = "known-code-edition-term";
                return true;
            }

            return false;
        }

        private static string MakeMappingKey(string currentText, string candidate, string layer)
        {
            return layer + "\u001F" + currentText + "\u001F" + candidate;
        }

        private static string MakeCurrentLayerKey(string currentText, string layer)
        {
            return layer + "\u001F" + currentText;
        }
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
