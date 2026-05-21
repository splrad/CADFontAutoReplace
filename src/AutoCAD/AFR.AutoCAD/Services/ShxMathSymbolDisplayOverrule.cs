using System.Collections.Concurrent;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;

namespace AFR.Services;

/// <summary>
/// Draws SHX-missing single-character '≥' without changing DBText content or text style.
/// </summary>
internal static class ShxMathSymbolDisplayOverrule
{
    private const string GreaterEqual = "\u2265";
    private const string TxtShx = "txt.shx";
    private static readonly HashSet<string> KnownMissingGreaterEqualBigFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "hztxt.shx",
        "tssdchn.shx"
    };

    private static readonly object SyncRoot = new();
    private static readonly ConcurrentDictionary<ObjectId, bool> StyleRequirementCache = new();
    private static DrawableOverrule? _overrule;
    private static RXClass? _dbTextClass;
    private static bool _installed;

    public static void ClearCache()
    {
        StyleRequirementCache.Clear();
    }

    public static void Install()
    {
        lock (SyncRoot)
        {
            if (_installed)
                return;

            ClearCache();
            _dbTextClass = RXClass.GetClass(typeof(DBText));
            _overrule = new GreaterEqualDbTextOverrule();
            _overrule.SetCustomFilter();
            Overrule.AddOverrule(_dbTextClass, _overrule, false);
            Overrule.Overruling = true;
            _installed = true;
            DiagnosticLogger.Log("SHX数学符号显示", "已安装 DBText 单字符 ≥ 显示补绘，仅作用于已验证缺字的 SHX 字体组合。");
        }
    }

    public static void Uninstall()
    {
        lock (SyncRoot)
        {
            if (!_installed || _dbTextClass == null || _overrule == null)
                return;

            try
            {
                Overrule.RemoveOverrule(_dbTextClass, _overrule);
            }
            catch (System.Exception ex)
            {
                DiagnosticLogger.Log("SHX数学符号显示", $"卸载 Overrule 失败（已忽略）: {ex.Message}");
            }
            finally
            {
                ClearCache();
                _overrule = null;
                _dbTextClass = null;
                _installed = false;
                DiagnosticLogger.Log("SHX数学符号显示", "已卸载 DBText 单字符 ≥ 显示补绘。");
            }
        }
    }

    private sealed class GreaterEqualDbTextOverrule : DrawableOverrule
    {
        public override bool IsApplicable(RXObject overruledSubject)
        {
            return overruledSubject is DBText dbText
                   && IsSingleGreaterEqual(dbText)
                   && RequiresSupplementalGreaterEqualGlyph(dbText);
        }

        public override bool WorldDraw(Drawable drawable, WorldDraw wd)
        {
            if (drawable is not DBText dbText || !IsSingleGreaterEqual(dbText))
                return base.WorldDraw(drawable, wd);

            DrawGreaterEqual(dbText, wd);
            return true;
        }
    }

    private static bool IsSingleGreaterEqual(DBText dbText)
    {
        return string.Equals(dbText.TextString, GreaterEqual, StringComparison.Ordinal);
    }

    private static bool RequiresSupplementalGreaterEqualGlyph(DBText dbText)
    {
        if (dbText.TextStyleId.IsNull || dbText.Database == null)
            return false;

        ObjectId styleId = dbText.TextStyleId;
        Database db = dbText.Database;
        return StyleRequirementCache.GetOrAdd(
            styleId,
            _ => ComputeSupplementalGreaterEqualRequirement(db, styleId));
    }

    private static bool ComputeSupplementalGreaterEqualRequirement(Database db, ObjectId styleId)
    {
        try
        {
            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(styleId, OpenMode.ForRead, false, true) is not TextStyleTableRecord style)
                return false;

            if (style.IsShapeFile)
                return false;

            string fileName = NormalizeFontName(style.FileName);
            string bigFontName = NormalizeFontName(style.BigFontFileName);

            // Fail open: do not take over arbitrary SHX fonts. If a user has a SHX/BigFont
            // that already contains U+2265, AutoCAD's native renderer must remain in control.
            return string.Equals(fileName, TxtShx, StringComparison.OrdinalIgnoreCase)
                   && KnownMissingGreaterEqualBigFonts.Contains(bigFontName);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeFontName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string fileName = Path.GetFileName(value!.Trim().TrimStart('@'));
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        return Path.HasExtension(fileName) ? fileName : fileName + ".shx";
    }

    private static void DrawGreaterEqual(DBText dbText, WorldDraw wd)
    {
        double height = dbText.Height;
        if (height <= 0.0)
            return;

        Vector3d normal = SafeNormal(dbText.Normal);
        Vector3d xAxis = Vector3d.XAxis.TransformBy(dbText.Ecs).GetNormal();
        xAxis = xAxis.RotateBy(dbText.Rotation, normal).GetNormal();
        Vector3d yAxis = normal.CrossProduct(xAxis).GetNormal();

        if (dbText.IsMirroredInX)
            xAxis = -xAxis;
        if (dbText.IsMirroredInY)
            yAxis = -yAxis;

        double widthFactor = dbText.WidthFactor > 0.0 ? dbText.WidthFactor : 1.0;
        double width = height * widthFactor * 0.82;
        Point3d origin = GetDrawOrigin(dbText, xAxis, yAxis, width, height);

        ApplyTraits(dbText, wd);

        DrawLine(wd, origin, xAxis, yAxis, width, height, 0.14, 0.80, 0.86, 0.52);
        DrawLine(wd, origin, xAxis, yAxis, width, height, 0.86, 0.52, 0.14, 0.24);
        DrawLine(wd, origin, xAxis, yAxis, width, height, 0.14, 0.08, 0.86, 0.08);
    }

    private static Point3d GetDrawOrigin(DBText dbText, Vector3d xAxis, Vector3d yAxis, double width, double height)
    {
        Point3d origin = dbText.Position;

        if (!dbText.IsDefaultAlignment)
            origin = dbText.AlignmentPoint;

        double xOffset = dbText.HorizontalMode switch
        {
            TextHorizontalMode.TextCenter => -width * 0.5,
            TextHorizontalMode.TextMid => -width * 0.5,
            TextHorizontalMode.TextRight => -width,
            TextHorizontalMode.TextAlign => -width * 0.5,
            TextHorizontalMode.TextFit => -width * 0.5,
            _ => 0.0
        };

        double yOffset = dbText.VerticalMode switch
        {
            TextVerticalMode.TextVerticalMid => -height * 0.42,
            TextVerticalMode.TextBottom => 0.0,
            TextVerticalMode.TextTop => -height * 0.84,
            _ => 0.0
        };

        return origin + (xAxis * xOffset) + (yAxis * yOffset);
    }

    private static Vector3d SafeNormal(Vector3d normal)
    {
        try
        {
            if (normal.Length > 0.0)
                return normal.GetNormal();
        }
        catch
        {
            // Fall through to WCS normal.
        }

        return Vector3d.ZAxis;
    }

    private static void ApplyTraits(DBText dbText, WorldDraw wd)
    {
        SubEntityTraits traits = wd.SubEntityTraits;
        traits.Layer = dbText.LayerId;
        traits.LineWeight = dbText.LineWeight;
        traits.TrueColor = dbText.EntityColor;
    }

    private static void DrawLine(
        WorldDraw wd,
        Point3d origin,
        Vector3d xAxis,
        Vector3d yAxis,
        double width,
        double height,
        double x1,
        double y1,
        double x2,
        double y2)
    {
        Point3d start = origin + (xAxis * (width * x1)) + (yAxis * (height * y1));
        Point3d end = origin + (xAxis * (width * x2)) + (yAxis * (height * y2));
        wd.Geometry.WorldLine(start, end);
    }
}
