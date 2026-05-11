using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AFR.DbTextRepairModel;

internal static class DbTextRepairModelMergeEngine
{
    private const string MutexName = "Global\\CADFontAutoReplace_DbTextRepairModel";

    public static DbTextRepairModelMergeReport MergeDirectory(
        string directoryPath,
        string embeddedJsonl,
        string embeddedSourceSetId)
    {
        var report = new DbTextRepairModelMergeReport { DirectoryPath = directoryPath };
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            report.Error = "模型目录为空。";
            return report;
        }

        report.CanonicalPath = Path.Combine(directoryPath, DbTextRepairModelConstants.CanonicalFileName);
        report.EmbeddedDatasetHash = DbTextRepairModelJsonl.ComputeTextHash(embeddedJsonl ?? string.Empty);

        using var mutex = new Mutex(false, MutexName);
        bool lockTaken = false;
        try
        {
            lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(10));
            if (!lockTaken)
            {
                report.Error = "等待模型文件锁超时。";
                return report;
            }

            Directory.CreateDirectory(directoryPath);
            MergeDirectoryCore(directoryPath, embeddedJsonl ?? string.Empty, embeddedSourceSetId, report);
            return report;
        }
        catch (Exception ex)
        {
            report.Error = $"{ex.GetType().Name}: {ex.Message}";
            return report;
        }
        finally
        {
            if (lockTaken)
            {
                try { mutex.ReleaseMutex(); }
                catch { }
            }
        }
    }

    public static DbTextRepairModelIndex LoadIndex(string canonicalPath, out DbTextRepairModelMergeReport report)
    {
        report = new DbTextRepairModelMergeReport
        {
            CanonicalPath = canonicalPath,
            DirectoryPath = Path.GetDirectoryName(canonicalPath) ?? string.Empty
        };

        IReadOnlyList<DbTextRepairModelRecord> records =
            DbTextRepairModelJsonl.ReadFile(canonicalPath, out string error);
        if (!string.IsNullOrEmpty(error))
            report.Error = error;

        report.ExistingRecords = records.Count;
        report.ConflictKeys = records.Count(r => r.IsConflict);
        return new DbTextRepairModelIndex(records);
    }

    public static DbTextRepairModelMergeReport AppendRecord(string canonicalPath, DbTextRepairModelRecord record)
    {
        var report = new DbTextRepairModelMergeReport
        {
            CanonicalPath = canonicalPath,
            DirectoryPath = Path.GetDirectoryName(canonicalPath) ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            report.Error = "模型文件路径为空。";
            return report;
        }

        using var mutex = new Mutex(false, MutexName);
        bool lockTaken = false;
        try
        {
            lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(10));
            if (!lockTaken)
            {
                report.Error = "等待模型文件锁超时。";
                return report;
            }

            string directory = Path.GetDirectoryName(canonicalPath) ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(directory);
            DbTextRepairModelJsonl.Normalize(record);
            File.AppendAllText(
                canonicalPath,
                DbTextRepairModelJsonl.Serialize(record) + Environment.NewLine,
                new UTF8Encoding(false));
            report.WrittenRecords = 1;
            return report;
        }
        catch (Exception ex)
        {
            report.Error = $"{ex.GetType().Name}: {ex.Message}";
            return report;
        }
        finally
        {
            if (lockTaken)
            {
                try { mutex.ReleaseMutex(); }
                catch { }
            }
        }
    }

    private static void MergeDirectoryCore(
        string directoryPath,
        string embeddedJsonl,
        string embeddedSourceSetId,
        DbTextRepairModelMergeReport report)
    {
        var allRecords = new List<DbTextRepairModelRecord>();
        var importFiles = new List<string>();
        string canonicalPath = report.CanonicalPath;

        if (File.Exists(canonicalPath))
        {
            IReadOnlyList<DbTextRepairModelRecord> existing =
                DbTextRepairModelJsonl.ReadFile(canonicalPath, out string error);
            if (!string.IsNullOrEmpty(error))
            {
                MarkCorrupt(canonicalPath);
                report.CorruptFiles++;
            }
            else
            {
                report.ExistingRecords = existing.Count;
                allRecords.AddRange(existing);
            }
        }

        foreach (string file in Directory.GetFiles(directoryPath, DbTextRepairModelConstants.ImportSearchPattern)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(file, canonicalPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.IndexOf(".corrupt.", StringComparison.OrdinalIgnoreCase) >= 0
                || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            IReadOnlyList<DbTextRepairModelRecord> imported =
                DbTextRepairModelJsonl.ReadFile(file, out string error);
            if (!string.IsNullOrEmpty(error))
            {
                MarkCorrupt(file);
                report.CorruptFiles++;
                continue;
            }

            report.ImportedRecords += imported.Count;
            allRecords.AddRange(imported);
            importFiles.Add(file);
        }

        if (!string.IsNullOrWhiteSpace(embeddedJsonl))
        {
            IReadOnlyList<DbTextRepairModelRecord> embedded =
                DbTextRepairModelJsonl.ReadText(embeddedJsonl, embeddedSourceSetId, out string error);
            if (string.IsNullOrEmpty(error))
            {
                report.EmbeddedRecords = embedded.Count;
                allRecords.AddRange(embedded);
                allRecords.Add(BuildEmbeddedMeta(embeddedSourceSetId, report.EmbeddedDatasetHash));
            }
        }

        List<DbTextRepairModelRecord> normalized = NormalizeAndDedupe(allRecords, report);
        AddConflictRecords(normalized, report);
        WriteCanonical(canonicalPath, normalized);

        foreach (string file in importFiles)
        {
            try
            {
                File.Delete(file);
                report.DeletedImportFiles++;
            }
            catch
            {
                // Import files are already merged. A failed cleanup should not fail installation.
            }
        }

        report.WrittenRecords = normalized.Count;
    }

    private static List<DbTextRepairModelRecord> NormalizeAndDedupe(
        IEnumerable<DbTextRepairModelRecord> records,
        DbTextRepairModelMergeReport report)
    {
        var byId = new Dictionary<string, DbTextRepairModelRecord>(StringComparer.Ordinal);

        foreach (DbTextRepairModelRecord record in records)
        {
            DbTextRepairModelJsonl.Normalize(record);
            if (string.IsNullOrEmpty(record.RecordId))
                continue;

            if (byId.ContainsKey(record.RecordId))
            {
                report.DuplicateRecords++;
                continue;
            }

            byId.Add(record.RecordId, record);
        }

        return byId.Values
            .OrderBy(r => SortRank(r.RecordType))
            .ThenBy(r => r.DrawingSha256, StringComparer.Ordinal)
            .ThenBy(r => r.Handle, StringComparer.Ordinal)
            .ThenBy(r => r.TimestampUtc, StringComparer.Ordinal)
            .ThenBy(r => r.RecordId, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddConflictRecords(List<DbTextRepairModelRecord> records, DbTextRepairModelMergeReport report)
    {
        var existingConflictKeys = new HashSet<string>(
            records.Where(r => r.IsConflict)
                .Select(r => string.IsNullOrEmpty(r.ConflictKey) ? r.RepairKey : r.ConflictKey)
                .Where(k => !string.IsNullOrEmpty(k)),
            StringComparer.Ordinal);

        var conflictKeys = records
            .Where(r => r.IsLabel)
            .GroupBy(DbTextRepairModelJsonl.GetRepairKey, StringComparer.Ordinal)
            .Where(g => !string.IsNullOrEmpty(g.Key)
                        && g.Select(DbTextRepairModelJsonl.GetDecisionSignature)
                            .Distinct(StringComparer.Ordinal)
                            .Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (string key in conflictKeys)
        {
            if (!existingConflictKeys.Add(key))
                continue;

            records.Add(new DbTextRepairModelRecord
            {
                RecordType = "conflict",
                SourceSetId = "merge-engine",
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                ConflictKey = key,
                RepairKey = key,
                Action = "abstain",
                Note = "Conflicting labels for the same DBText repair key; automatic repair is blocked."
            });
        }

        report.ConflictKeys = existingConflictKeys.Count;
    }

    private static DbTextRepairModelRecord BuildEmbeddedMeta(string sourceSetId, string hash)
    {
        return new DbTextRepairModelRecord
        {
            RecordType = "meta",
            RecordId = "meta-embedded-" + (hash.Length >= 16 ? hash.Substring(0, 16) : hash),
            SourceSetId = string.IsNullOrEmpty(sourceSetId) ? "embedded" : sourceSetId,
            TimestampUtc = "1970-01-01T00:00:00.0000000Z",
            EmbeddedDatasetHash = hash,
            Note = "Embedded dataset merged."
        };
    }

    private static void WriteCanonical(string canonicalPath, IReadOnlyList<DbTextRepairModelRecord> records)
    {
        string directory = Path.GetDirectoryName(canonicalPath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, "DbTextRepairModel.merge.tmp");
        string backupPath = Path.Combine(directory, "DbTextRepairModel.backup.tmp");

        var builder = new StringBuilder(records.Count * 256);
        for (int i = 0; i < records.Count; i++)
            builder.AppendLine(DbTextRepairModelJsonl.Serialize(records[i]));

        File.WriteAllText(tempPath, builder.ToString(), new UTF8Encoding(false));

        if (File.Exists(canonicalPath))
        {
            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Replace(tempPath, canonicalPath, backupPath);
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                return;
            }
            catch
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                try
                {
                    File.Move(canonicalPath, backupPath);
                    File.Move(tempPath, canonicalPath);
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    return;
                }
                catch
                {
                    if (!File.Exists(canonicalPath) && File.Exists(backupPath))
                    {
                        try { File.Move(backupPath, canonicalPath); }
                        catch { }
                    }

                    throw;
                }
            }
        }

        File.Move(tempPath, canonicalPath);
    }

    private static void MarkCorrupt(string path)
    {
        try
        {
            string corruptPath = path + ".corrupt." + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "." + Guid.NewGuid().ToString("N")[..8] + ".jsonl";
            File.Move(path, corruptPath);
        }
        catch
        {
            // Keep the original file if renaming fails.
        }
    }

    private static int SortRank(string recordType)
    {
        return recordType switch
        {
            "meta" => 0,
            DbTextRepairModelConstants.RecordTypeNeuralParameters => 1,
            "params" => 1,
            DbTextRepairModelConstants.RecordTypeLabel => 2,
            DbTextRepairModelConstants.RecordTypeConflict => 3,
            _ => 9
        };
    }
}
