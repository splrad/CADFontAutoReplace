#if DEBUG
using Autodesk.AutoCAD.DatabaseServices;
using AFR.FontMapping;

namespace AFR.Services;

/// <summary>
/// DBText native 解码证据修复服务。
/// <para>
/// 只在 DEBUG 构建中启用，并且只接受 <see cref="TextEditorDbcsDecodeHook"/>
/// 在本次 AutoCAD 运行时已经观察到的 native DBCS 解码证据。该服务不依据乱码外观、
/// 文本语义、DWG 文件头猜测或用户指定编码做判断。
/// </para>
/// </summary>
internal static class DbTextEncodingRepairService
{
    private const int RepairLogLimit = 80;
    private const int SkipLogLimit = 40;
    private const int InvalidDecodedLogLimit = 40;

    /// <summary>
    /// 修复当前数据库中证据全覆盖的 <see cref="DBText"/> 文本。
    /// <para>
    /// 探针模式下保持只读；修复模式下仅处理 DBText，不修改 MText、属性定义或其它实体类型。
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
        int repaired = 0;
        int skipped = 0;
        int invalidDecoded = 0;
        int errors = 0;

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
                    if (!TextEditorDbcsDecodeHook.TryDecodeWithObservedEvidence(
                            original,
                            out string decoded,
                            out string reason,
                            allowNativeExpansion: true))
                    {
                        skipped++;
                        if (skipped <= SkipLogLimit && reason.StartsWith("partial", StringComparison.Ordinal))
                            DiagnosticLogger.Log("DBText证据修复", $"跳过 Handle={dbText.Handle}: {reason}");
                        continue;
                    }

                    if (!IsValidNativeDecodedText(decoded, out string invalidReason))
                    {
                        skipped++;
                        invalidDecoded++;
                        if (invalidDecoded <= InvalidDecodedLogLimit)
                        {
                            DiagnosticLogger.Log(
                                "DBText证据修复",
                                $"跳过 Handle={dbText.Handle}: native decoded text rejected, {invalidReason}");
                        }

                        continue;
                    }

                    dbText.UpgradeOpen();
                    dbText.TextString = decoded;
                    repaired++;

                    if (repaired <= RepairLogLimit)
                    {
                        DiagnosticLogger.Log(
                            "DBText证据修复",
                            $"已修复 Handle={dbText.Handle}, Len={original.Length}->{decoded.Length}, " +
                            $"Coverage={reason}, Text='{EscapeForLog(decoded)}'");
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
            $"扫描={scanned}, 修复={repaired}, 跳过={skipped}, 无效解码={invalidDecoded}, 错误={errors}");
        return repaired;
    }

    private static bool IsValidNativeDecodedText(string text, out string reason)
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

            if (ch >= '\uE000' && ch <= '\uF8FF')
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

    private static string EscapeForLog(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }
}
#endif
