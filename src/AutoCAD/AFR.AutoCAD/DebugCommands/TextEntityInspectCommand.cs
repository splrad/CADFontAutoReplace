#if DEBUG
using System.Reflection;
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
            string report = BuildReport(obj, tr, SafeGetContainers(result));
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

    private static string BuildReport(DBObject obj, Transaction tr, ObjectId[] containers)
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
        AppendKnownText(lines, obj, tr);
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

    private static void AppendKnownText(List<string> lines, DBObject obj, Transaction tr)
    {
        switch (obj)
        {
            case DBText dbText:
                lines.Add($"DBText.TextString: {Escape(dbText.TextString)}");
                AppendObservedDecodePreview(lines, dbText.TextString);
                AppendNativeDbTextEvidence(lines, dbText);
                lines.Add($"TextStyleId: {Safe(() => DescribeTextStyle(dbText.TextStyleId, tr))}");
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

    private static void AppendNativeDbTextEvidence(List<string> lines, DBText dbText)
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
        lines.Add($"NativeProvenanceTextMatchesCurrent: {textMatches}");
        if (!textMatches)
        {
            lines.Add($"NativeProvenanceText: {Escape(provenance.NativeText)}");
            lines.Add("NativeRepairDecision: reject native-text-mismatch");
            return;
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
                lines.Add("NativeExactRepairDecision: reject already-exact-native-text");
            }
            else if (DbTextEncodingRepairService.TryValidateNativeDecodedTextForRepair(
                         decoded,
                         out string validReason,
                         allowPrivateUse: true))
            {
                lines.Add("NativeExactRepairDecision: allow");
            }
            else
            {
                lines.Add($"NativeExactRepairDecision: reject {validReason}");
            }

            return;
        }

        lines.Add($"NativeExactDecodeCoverage: {reason}");
        lines.Add("NativeExactRepairDecision: reject no-full-native-dbcs-sequence");

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
            if (string.Equals(text, observedDecoded, StringComparison.Ordinal))
            {
                lines.Add("ObservedFallbackRepairDecision: reject already-exact-native-text");
            }
            else if (DbTextEncodingRepairService.TryValidateNativeDecodedTextForRepair(
                         observedDecoded,
                         out string observedValidReason))
            {
                lines.Add("ObservedFallbackRepairDecision: allow");
            }
            else
            {
                lines.Add($"ObservedFallbackRepairDecision: reject {observedValidReason}");
            }
        }
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

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (System.Exception ex) { return "<读取失败:" + ex.GetType().Name + ":" + ex.Message + ">"; }
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
#endif
