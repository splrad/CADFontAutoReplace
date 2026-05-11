using System;
using System.Collections.Generic;
using System.Linq;

namespace AFR.DbTextRepairModel;

internal static class DbTextRepairModelConstants
{
    public const string SchemaVersion = "dbtext-repair-model-v1";
    public const string LegacyManualLabelSchemaVersion = "dbtext-manual-label-v1";
    public const string CanonicalFileName = "DbTextRepairModel.jsonl";
    public const string ImportSearchPattern = "DbTextRepairModel*.jsonl";
    public const string ResourceName = "AFR.DbTextRepairModel.jsonl";
    public const string ActionRepair = "repair";
    public const string ActionKeep = "keep";
    public const string ActionGlyphIssue = "glyph-issue";
}

internal sealed class DbTextRepairModelRecord
{
    public string SchemaVersion { get; set; } = DbTextRepairModelConstants.SchemaVersion;
    public string RecordType { get; set; } = "label";
    public string RecordId { get; set; } = string.Empty;
    public string SourceSetId { get; set; } = string.Empty;
    public string TimestampUtc { get; set; } = string.Empty;
    public string DrawingPath { get; set; } = string.Empty;
    public string DrawingFileName { get; set; } = string.Empty;
    public long DrawingLength { get; set; }
    public string DrawingLastWriteUtc { get; set; } = string.Empty;
    public string DrawingSha256 { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string OwnerBlockName { get; set; } = string.Empty;
    public string TextStyleName { get; set; } = string.Empty;
    public string TextStyleFileName { get; set; } = string.Empty;
    public string TextStyleBigFontFileName { get; set; } = string.Empty;
    public string TextStyleTypeFace { get; set; } = string.Empty;
    public string CurrentText { get; set; } = string.Empty;
    public string CandidateText { get; set; } = string.Empty;
    public string SelectedText { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string RepairKey { get; set; } = string.Empty;
    public string ConflictKey { get; set; } = string.Empty;
    public string EmbeddedDatasetHash { get; set; } = string.Empty;
    public string TrainingDataHash { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public bool IsLabel => string.Equals(RecordType, "label", StringComparison.OrdinalIgnoreCase);
    public bool IsConflict => string.Equals(RecordType, "conflict", StringComparison.OrdinalIgnoreCase);
}

internal sealed class DbTextRepairModelMergeReport
{
    public string DirectoryPath { get; set; } = string.Empty;
    public string CanonicalPath { get; set; } = string.Empty;
    public int ExistingRecords { get; set; }
    public int EmbeddedRecords { get; set; }
    public int ImportedRecords { get; set; }
    public int WrittenRecords { get; set; }
    public int DuplicateRecords { get; set; }
    public int ConflictKeys { get; set; }
    public int DeletedImportFiles { get; set; }
    public int CorruptFiles { get; set; }
    public string EmbeddedDatasetHash { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;

    public bool Success => string.IsNullOrEmpty(Error);

    public string ToSummary()
    {
        if (!Success)
            return $"模型合并失败: {Error}";

        return
            $"Path='{CanonicalPath}', Existing={ExistingRecords}, Embedded={EmbeddedRecords}, " +
            $"Imported={ImportedRecords}, Written={WrittenRecords}, Duplicates={DuplicateRecords}, " +
            $"Conflicts={ConflictKeys}, DeletedImports={DeletedImportFiles}, Corrupt={CorruptFiles}";
    }
}

internal sealed class DbTextRepairModelIndex
{
    private readonly Dictionary<string, DbTextRepairModelRecord> _latestByExactKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conflictKeys = new(StringComparer.Ordinal);

    public DbTextRepairModelIndex(IEnumerable<DbTextRepairModelRecord> records)
    {
        var labelGroups = new Dictionary<string, List<DbTextRepairModelRecord>>(StringComparer.Ordinal);

        foreach (DbTextRepairModelRecord record in records)
        {
            if (record.IsConflict)
            {
                string conflictKey = string.IsNullOrEmpty(record.ConflictKey) ? record.RepairKey : record.ConflictKey;
                if (!string.IsNullOrEmpty(conflictKey))
                    _conflictKeys.Add(conflictKey);
                continue;
            }

            if (!record.IsLabel)
                continue;

            string key = DbTextRepairModelJsonl.GetRepairKey(record);
            if (string.IsNullOrEmpty(key))
                continue;

            if (!labelGroups.TryGetValue(key, out List<DbTextRepairModelRecord>? group))
            {
                group = new List<DbTextRepairModelRecord>();
                labelGroups[key] = group;
            }

            group.Add(record);
        }

        foreach (var pair in labelGroups)
        {
            int distinctDecisionCount = pair.Value
                .Select(DbTextRepairModelJsonl.GetDecisionSignature)
                .Distinct(StringComparer.Ordinal)
                .Count();

            if (distinctDecisionCount > 1)
            {
                _conflictKeys.Add(pair.Key);
                continue;
            }

            _latestByExactKey[pair.Key] = pair.Value
                .OrderBy(r => r.TimestampUtc, StringComparer.Ordinal)
                .Last();
        }
    }

    public int LabelCount => _latestByExactKey.Count;
    public int ConflictCount => _conflictKeys.Count;

    public bool TryFindExact(
        string drawingSha256,
        string handle,
        string currentText,
        string candidateText,
        out DbTextRepairModelRecord record,
        out bool hasConflict)
    {
        string candidateKey = DbTextRepairModelJsonl.MakeRepairKey(drawingSha256, handle, currentText, candidateText);
        string currentKey = DbTextRepairModelJsonl.MakeRepairKey(drawingSha256, handle, currentText, string.Empty);

        hasConflict = _conflictKeys.Contains(candidateKey) || _conflictKeys.Contains(currentKey);
        if (hasConflict)
        {
            record = new DbTextRepairModelRecord();
            return false;
        }

        if (!string.IsNullOrEmpty(candidateText) && _latestByExactKey.TryGetValue(candidateKey, out DbTextRepairModelRecord? candidateRecord))
        {
            record = candidateRecord;
            return true;
        }

        if (_latestByExactKey.TryGetValue(currentKey, out DbTextRepairModelRecord? currentRecord))
        {
            record = currentRecord;
            return true;
        }

        record = new DbTextRepairModelRecord();
        return false;
    }
}
