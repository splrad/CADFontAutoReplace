#if DEBUG
using Autodesk.AutoCAD.DatabaseServices;

namespace AFR.Services;

/// <summary>
/// DEBUG compatibility shim for the retired pre-Release DBText native repair path.
/// Release DBText labels are stored through AFR.Services.DbTextRepair.DbTextRepairModelStore.
/// </summary>
internal static class DbTextManualLabelService
{
    public const string ActionRepair = "repair";
    public const string ActionKeep = "keep";
    public const string ActionGlyphIssue = "glyph-issue";

    public static DbTextManualLabelIndex LoadIndex(Database db)
    {
        return new DbTextManualLabelIndex(string.Empty);
    }
}

internal sealed class DbTextManualLabelIndex
{
    public DbTextManualLabelIndex(string drawingSha256)
    {
        DrawingSha256 = drawingSha256;
    }

    public string DrawingSha256 { get; }
    public int Count => 0;

    public bool TryFindCurrent(DBText dbText, string currentText, out DbTextManualLabelRecord record)
    {
        record = new DbTextManualLabelRecord();
        return false;
    }

    public bool TryFind(DBText dbText, string currentText, string candidateText, out DbTextManualLabelRecord record)
    {
        record = new DbTextManualLabelRecord();
        return false;
    }
}

internal sealed class DbTextManualLabelRecord
{
    public string TimestampUtc { get; set; } = string.Empty;
    public string SelectedText { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}
#endif
