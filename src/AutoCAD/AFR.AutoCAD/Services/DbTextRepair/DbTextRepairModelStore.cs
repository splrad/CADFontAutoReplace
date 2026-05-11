using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AFR.DbTextRepairModel;
using AFR.Services;

namespace AFR.Services.DbTextRepair;

internal static class DbTextRepairModelStore
{
    private static readonly object Sync = new();
    private static bool _ready;
    private static string _activeDirectory = string.Empty;
    private static string _canonicalPath = string.Empty;
    private static DbTextRepairModelMergeReport _lastMergeReport = new();
    private static string _lastNeuralTrainingStatus = string.Empty;

    public static string ActiveDirectory
    {
        get { EnsureReady(); return _activeDirectory; }
    }

    public static string CanonicalPath
    {
        get { EnsureReady(); return _canonicalPath; }
    }

    public static DbTextRepairModelMergeReport LastMergeReport
    {
        get { EnsureReady(); return _lastMergeReport; }
    }

    public static string LastNeuralTrainingStatus
    {
        get { EnsureReady(); return _lastNeuralTrainingStatus; }
    }

    public static DbTextRepairModelIndex LoadIndex(out DbTextRepairModelMergeReport report)
    {
        EnsureReady();
        return DbTextRepairModelMergeEngine.LoadIndex(_canonicalPath, out report);
    }

    public static DbTextRepairModelMergeReport ForceMerge()
    {
        lock (Sync)
        {
            _ready = false;
        }

        EnsureReady();
        return LastMergeReport;
    }

    public static void AppendLabel(DbTextRepairModelRecord record)
    {
        AppendRecord(record);
    }

    public static void AppendLabels(IReadOnlyList<DbTextRepairModelRecord> records)
    {
        AppendRecords(records);
    }

    public static void AppendRecord(DbTextRepairModelRecord record)
    {
        AppendRecords(new[] { record });
    }

    public static void AppendRecords(IReadOnlyList<DbTextRepairModelRecord> records)
    {
        EnsureReady();
        if (records.Count == 0)
            return;

        lock (Sync)
        {
            AppendRecordsCore(records);
            _ready = false;
        }

        EnsureReady();
    }

    public static void EnsureReady()
    {
        lock (Sync)
        {
            if (_ready)
                return;

            _activeDirectory = ResolveActiveDirectory();
            _canonicalPath = Path.Combine(_activeDirectory, DbTextRepairModelConstants.CanonicalFileName);
            string embedded = ReadEmbeddedDataset();
            _lastMergeReport = DbTextRepairModelMergeEngine.MergeDirectory(
                _activeDirectory,
                embedded,
                "embedded-" + PluginVersionService.GetBuildId());
            _lastNeuralTrainingStatus = _lastMergeReport.Success
                ? EnsureFreshNeuralParametersCore(embedded)
                : "跳过（模型合并失败）";
            _ready = true;
        }
    }

    private static string EnsureFreshNeuralParametersCore(string embedded)
    {
        DbTextRepairModelIndex index = DbTextRepairModelMergeEngine.LoadIndex(_canonicalPath, out _);
        if (index.RawLabelRecordCount == 0)
            return "跳过（无人工标签）";

        if (index.HasActiveNeuralParameters)
            return "已是最新";

        DbTextNeuralTrainingResult result = DbTextNeuralTrainer.Train(index.Labels, BuildNeuralSourceSetId());
        if (!result.Success || result.Record == null)
        {
            DiagnosticLogger.Log("DBText模型", $"自动训练跳过: {result.Error}");
            return $"跳过（{result.Error}）";
        }

        AppendRecordsCore(new[] { result.Record });
        _lastMergeReport = DbTextRepairModelMergeEngine.MergeDirectory(
            _activeDirectory,
            embedded,
            "embedded-" + PluginVersionService.GetBuildId());

        DbTextRepairModelIndex updatedIndex = DbTextRepairModelMergeEngine.LoadIndex(_canonicalPath, out _);
        string status = updatedIndex.HasActiveNeuralParameters ? "完成" : "已写入但未激活";
        DiagnosticLogger.Log(
            "DBText模型",
            $"自动训练{status}: TrainingDataHash={result.Record.TrainingDataHash}, " +
            $"TrainingRecordCount={result.Record.TrainingRecordCount}, Summary={result.Summary}");
        return status;
    }

    private static void AppendRecordsCore(IReadOnlyList<DbTextRepairModelRecord> records)
    {
        DbTextRepairModelMergeReport report = DbTextRepairModelMergeEngine.AppendRecords(_canonicalPath, records);
        if (!report.Success)
            throw new IOException("写入 DBText 修复模型失败: " + report.Error);
    }

    private static string BuildNeuralSourceSetId()
    {
        string machine = Environment.MachineName ?? "MACHINE";
        string user = Environment.UserName ?? "USER";
        string basis = machine + "\u001F" + user;
        return "nn-auto-" + DbTextRepairModelJsonl.ComputeTextHash(basis).Substring(0, 10);
    }

    private static string ResolveActiveDirectory()
    {
        string? repoRoot = TryFindRepoRootFromAssembly();
        if (!string.IsNullOrEmpty(repoRoot))
        {
            string dataDir = Path.Combine(repoRoot, "data");
            if (Directory.Exists(dataDir))
                return dataDir;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CADFontAutoReplace");
    }

    private static string? TryFindRepoRootFromAssembly()
    {
        string? directory = Path.GetDirectoryName(typeof(DbTextRepairModelStore).Assembly.Location);
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "CADFontAutoReplace.slnx"))
                || Directory.Exists(Path.Combine(directory, ".git")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    private static string ReadEmbeddedDataset()
    {
        Assembly assembly = typeof(DbTextRepairModelStore).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(DbTextRepairModelConstants.ResourceName);
        if (stream == null)
            return string.Empty;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
