#if DEBUG
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.FontMapping;

namespace AFR.Services;

/// <summary>
/// DBText native 解码证据修复服务。
/// <para>
/// 只在 DEBUG 构建中启用。仅当 native DWG 读取阶段提供对象级 provenance，
/// 且该 provenance 能与当前托管 <see cref="DBText"/> 精确匹配时才写回。
/// </para>
/// </summary>
internal static class DbTextEncodingRepairService
{
    private const int CandidateLogLimit = 80;
    private const int SkipLogLimit = 40;
    private const int InvalidDecodedLogLimit = 40;
    private const int MaxLoggedTextChars = 80;

    /// <summary>
    /// 修复当前数据库中具备 native 对象级证据的 <see cref="DBText"/> 文本。
    /// <para>
    /// 不使用文字外观判断；只有 <c>DBText.UnmanagedObject + 0x8</c> 指向的 native
    /// <c>AcDbImpText</c> 与 DWG 读入阶段记录的 <c>AcDbImpText</c> 相同，且 native
    /// 读入后文本与当前 TextString 完全一致时才允许写回。
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
        int observedFallbackCount = 0;
        int privateUseSymbolPreservedCount = 0;
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
                    else if (TextEditorDbcsDecodeHook.TryDecodeWithObservedEvidence(
                                 original,
                                 provenance.CodePageId,
                                 out decoded,
                                 out reason,
                                 allowNativeExpansion: true))
                    {
                        observedFallbackCount++;
                        reason = "observed-fallback " + reason;
                        if (TryNormalizePrivateUseSymbolsFromOriginal(
                                original,
                                decoded,
                                out string normalizedDecoded,
                                out string normalizeReason))
                        {
                            decoded = normalizedDecoded;
                            reason += ", " + normalizeReason;
                            privateUseSymbolPreservedCount++;
                        }
                    }
                    else
                    {
                        skipped++;
                        if (partialSkipLogCount++ < SkipLogLimit && reason.StartsWith("partial", StringComparison.Ordinal))
                        {
                            DiagnosticLogger.Log(
                                "DBText证据修复",
                                $"跳过 Handle={dbText.Handle}: {reason}, " +
                                $"Text='{EscapeForLog(TrimForLog(original))}'");
                        }

                        continue;
                    }

                    if (string.Equals(original, decoded, StringComparison.Ordinal))
                    {
                        skipped++;
                        continue;
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
            $"exact序列={exactNativeSequenceCount}, observed回退={observedFallbackCount}, " +
            $"PUA符号保留={privateUseSymbolPreservedCount}, " +
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

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static string TrimForLog(string text)
    {
        if (text.Length <= MaxLoggedTextChars)
            return text;

        return text[..MaxLoggedTextChars] + "...";
    }
}
#endif
