using System;
using AFR.DbTextRepairModel;

namespace AFR.Services.DbTextRepair;

internal static class DbTextRepairPolicy
{
    public static bool CanAutoRepairByLabel(
        DbTextRepairModelRecord record,
        string currentText,
        out string selectedText,
        out string reason)
    {
        selectedText = string.Empty;
        reason = string.Empty;

        if (!string.Equals(record.Action, DbTextRepairModelConstants.ActionRepair, StringComparison.OrdinalIgnoreCase))
        {
            reason = "label-action-block";
            return false;
        }

        if (string.IsNullOrEmpty(record.SelectedText))
        {
            reason = "empty-selected-text";
            return false;
        }

        if (string.Equals(record.SelectedText, currentText, StringComparison.Ordinal))
        {
            reason = "selected-same-as-current";
            return false;
        }

        selectedText = record.SelectedText;
        reason = "exact-manual-label";
        return true;
    }
}
