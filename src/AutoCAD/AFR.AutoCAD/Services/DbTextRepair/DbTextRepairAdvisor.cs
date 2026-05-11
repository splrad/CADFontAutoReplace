using System;
using AFR.DbTextRepairModel;

namespace AFR.Services.DbTextRepair;

internal sealed class DbTextRepairAdvisor
{
    private readonly DbTextRepairModelIndex _index;

    public DbTextRepairAdvisor(DbTextRepairModelIndex index)
    {
        _index = index;
    }

    public DbTextRepairDecision Evaluate(
        DbTextDrawingIdentity drawing,
        string handle,
        string currentText,
        string candidateText)
    {
        if (_index.TryFindExact(
                drawing.Sha256,
                handle,
                currentText,
                candidateText,
                out DbTextRepairModelRecord record,
                out bool hasConflict))
        {
            if (DbTextRepairPolicy.CanAutoRepairByLabel(record, currentText, out string selectedText, out string reason))
                return DbTextRepairDecision.Repair(selectedText, reason);

            return DbTextRepairDecision.Block("label-" + reason);
        }

        if (hasConflict)
            return DbTextRepairDecision.Block("conflict");

        return DbTextRepairDecision.Abstain("no-exact-label");
    }
}

internal sealed class DbTextRepairDecision
{
    private DbTextRepairDecision(string action, string selectedText, string reason)
    {
        Action = action;
        SelectedText = selectedText;
        Reason = reason;
    }

    public string Action { get; }
    public string SelectedText { get; }
    public string Reason { get; }

    public bool ShouldRepair => string.Equals(Action, "repair", StringComparison.Ordinal);
    public bool IsBlocked => string.Equals(Action, "block", StringComparison.Ordinal);

    public static DbTextRepairDecision Repair(string selectedText, string reason) => new("repair", selectedText, reason);
    public static DbTextRepairDecision Block(string reason) => new("block", string.Empty, reason);
    public static DbTextRepairDecision Abstain(string reason) => new("abstain", string.Empty, reason);
}
