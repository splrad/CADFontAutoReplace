using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.FontMapping;
using AFR.GlyphCore.TextRepair;

namespace AFR.Services.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairEntitySnapshotBuilder
{
    public static GlyphCoreTextRepairContext BuildContext(
        Database db,
        Transaction tr,
        DBText dbText,
        GlyphCoreDrawingIdentity drawing)
    {
        TextStyleIdentity style = GetTextStyleIdentity(tr, dbText);
        var context = new GlyphCoreTextRepairContext
        {
            DrawingPath = drawing.Path,
            DrawingFileName = drawing.FileName,
            DrawingLength = drawing.Length,
            DrawingLastWriteUtc = drawing.LastWriteUtc,
            DrawingSha256 = drawing.Sha256,
            EntityType = "DBText",
            ObjectId = SafeObjectId(dbText.ObjectId),
            Handle = dbText.Handle.ToString(),
            Layer = Safe(() => dbText.Layer, string.Empty),
            OwnerBlockName = DescribeOwnerBlock(dbText, tr),
            TextStyleName = style.Name,
            TextStyleFileName = style.FileName,
            TextStyleBigFontFileName = style.BigFontFileName,
            TextStyleTypeFace = style.TypeFace,
            CurrentText = dbText.TextString ?? string.Empty,
            IsFromExternalReference = IsFromExternalReference(dbText, tr),
            HasLdFileFontEvidence = HasLdFileFontEvidence(style)
        };
        GlyphCoreNativeDbTextEvidenceProjector.TryRegister(dbText, drawing, context);
        GlyphCoreNativeDecodeEvidenceStore.ApplyEvidence(drawing, context);
        return context;
    }

    public static bool IsFromExternalReference(DBText dbText, Transaction tr)
    {
        try
        {
            if (tr.GetObject(dbText.OwnerId, OpenMode.ForRead, false, true) is BlockTableRecord owner)
                return owner.IsFromExternalReference || owner.IsDependent;
        }
        catch
        {
            // ignored
        }

        return false;
    }

    public static string DescribeOwnerBlock(DBText dbText, Transaction tr)
    {
        try
        {
            if (tr.GetObject(dbText.OwnerId, OpenMode.ForRead, false, true) is BlockTableRecord owner)
                return owner.Name;
        }
        catch
        {
            // ignored
        }

        return string.Empty;
    }

    private static TextStyleIdentity GetTextStyleIdentity(Transaction tr, DBText dbText)
    {
        try
        {
            if (tr.GetObject(dbText.TextStyleId, OpenMode.ForRead, false, true) is TextStyleTableRecord style)
            {
                string typeFace = string.Empty;
                try { typeFace = style.Font.TypeFace ?? string.Empty; }
                catch { typeFace = string.Empty; }

                return new TextStyleIdentity(
                    style.Name,
                    style.FileName ?? string.Empty,
                    style.BigFontFileName ?? string.Empty,
                    typeFace);
            }
        }
        catch
        {
            // ignored
        }

        return new TextStyleIdentity(string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static bool HasLdFileFontEvidence(TextStyleIdentity style)
    {
        var redirectLog = LdFileHook.GetRawRedirectLog();
        return ContainsFontEvidence(redirectLog, style.FileName)
               || ContainsFontEvidence(redirectLog, style.BigFontFileName)
               || ContainsFontEvidence(redirectLog, style.TypeFace);
    }

    private static bool ContainsFontEvidence(
        IReadOnlyDictionary<string, (string Replacement, int FontType)> redirectLog,
        string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        string normalized = System.IO.Path.GetFileName(fontName.TrimStart('@'));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (redirectLog.ContainsKey(normalized))
            return true;

        if (!normalized.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            && redirectLog.ContainsKey(normalized + ".shx"))
            return true;

        return false;
    }

    private static string SafeObjectId(ObjectId id)
    {
        try { return id.ToString(); }
        catch { return string.Empty; }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private readonly record struct TextStyleIdentity(
        string Name,
        string FileName,
        string BigFontFileName,
        string TypeFace);
}

