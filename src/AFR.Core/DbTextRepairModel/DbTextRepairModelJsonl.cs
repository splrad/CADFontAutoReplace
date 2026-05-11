using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AFR.DbTextRepairModel;

internal static class DbTextRepairModelJsonl
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    public static IReadOnlyList<DbTextRepairModelRecord> ReadFile(string path, out string error)
    {
        error = string.Empty;
        var records = new List<DbTextRepairModelRecord>();

        if (!File.Exists(path))
            return records;

        try
        {
            int lineNumber = 0;
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                DbTextRepairModelRecord record = ParseLine(line, $"file:{path}:{lineNumber}");
                records.Add(record);
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return records;
    }

    public static IReadOnlyList<DbTextRepairModelRecord> ReadText(string text, string sourceSetId, out string error)
    {
        error = string.Empty;
        var records = new List<DbTextRepairModelRecord>();
        if (string.IsNullOrWhiteSpace(text))
            return records;

        try
        {
            using var reader = new StringReader(text);
            int lineNumber = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                DbTextRepairModelRecord record = ParseLine(line, $"embedded:{sourceSetId}:{lineNumber}");
                if (string.IsNullOrEmpty(record.SourceSetId))
                    record.SourceSetId = sourceSetId;
                records.Add(record);
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return records;
    }

    public static string Serialize(DbTextRepairModelRecord record)
    {
        Normalize(record);
        return JsonConvert.SerializeObject(record, Formatting.None, JsonSettings);
    }

    public static string ComputeTextHash(string text)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
        return ToHex(hash);
    }

    public static string GetRepairKey(DbTextRepairModelRecord record)
    {
        if (!string.IsNullOrEmpty(record.RepairKey))
            return record.RepairKey;

        return MakeRepairKey(record.DrawingSha256, record.Handle, record.CurrentText, record.CandidateText);
    }

    public static string MakeRepairKey(string drawingSha256, string handle, string currentText, string candidateText)
    {
        if (string.IsNullOrEmpty(drawingSha256) || string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(currentText))
            return string.Empty;

        return string.Join(
            "\u001F",
            drawingSha256.ToUpperInvariant(),
            handle.ToUpperInvariant(),
            currentText,
            candidateText ?? string.Empty);
    }

    public static string GetDecisionSignature(DbTextRepairModelRecord record)
    {
        return string.Join(
            "\u001F",
            (record.Action ?? string.Empty).ToLowerInvariant(),
            record.SelectedText ?? string.Empty);
    }

    public static void Normalize(DbTextRepairModelRecord record)
    {
        if (string.IsNullOrEmpty(record.SchemaVersion))
            record.SchemaVersion = DbTextRepairModelConstants.SchemaVersion;
        if (string.IsNullOrEmpty(record.RecordType))
            record.RecordType = "label";
        if (string.IsNullOrEmpty(record.TimestampUtc))
            record.TimestampUtc = DateTime.UtcNow.ToString("O");
        if (record.IsLabel && string.IsNullOrEmpty(record.RepairKey))
            record.RepairKey = GetRepairKey(record);
        if (record.IsConflict && string.IsNullOrEmpty(record.ConflictKey))
            record.ConflictKey = record.RepairKey;
        if (string.IsNullOrEmpty(record.RecordId))
            record.RecordId = ComputeRecordId(record);
    }

    public static string ComputeRecordId(DbTextRepairModelRecord record)
    {
        string basis = string.Join(
            "\u001E",
            record.SchemaVersion,
            record.RecordType,
            record.SourceSetId,
            record.TimestampUtc,
            record.DrawingSha256,
            record.Handle,
            record.Layer,
            record.OwnerBlockName,
            record.TextStyleName,
            record.TextStyleFileName,
            record.TextStyleBigFontFileName,
            record.CurrentText,
            record.CandidateText,
            record.SelectedText,
            record.Action,
            record.ConflictKey,
            record.EmbeddedDatasetHash,
            record.Note);

        return record.RecordType + "-" + ComputeTextHash(basis).Substring(0, 24);
    }

    private static DbTextRepairModelRecord ParseLine(string line, string source)
    {
        JObject obj = JObject.Parse(line);
        string schema = obj.Value<string>("SchemaVersion") ?? string.Empty;

        DbTextRepairModelRecord? record = obj.ToObject<DbTextRepairModelRecord>();
        if (record == null)
            throw new InvalidDataException($"无法读取模型记录: {source}");

        if (string.Equals(schema, DbTextRepairModelConstants.LegacyManualLabelSchemaVersion, StringComparison.OrdinalIgnoreCase))
            record = ConvertLegacyManualLabel(record);

        Normalize(record);
        return record;
    }

    private static DbTextRepairModelRecord ConvertLegacyManualLabel(DbTextRepairModelRecord legacy)
    {
        legacy.SchemaVersion = DbTextRepairModelConstants.SchemaVersion;
        legacy.RecordType = "label";
        legacy.SourceSetId = string.IsNullOrEmpty(legacy.SourceSetId) ? "legacy-manual-label" : legacy.SourceSetId;
        legacy.RecordId = string.Empty;
        legacy.RepairKey = string.Empty;
        legacy.ConflictKey = string.Empty;
        legacy.EmbeddedDatasetHash = string.Empty;
        legacy.TrainingDataHash = string.Empty;
        return legacy;
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
            builder.Append(bytes[i].ToString("X2"));
        return builder.ToString();
    }
}
