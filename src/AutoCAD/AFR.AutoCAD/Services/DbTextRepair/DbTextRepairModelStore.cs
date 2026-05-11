using System;
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

    public static void AppendRecord(DbTextRepairModelRecord record)
    {
        EnsureReady();
        DbTextRepairModelJsonl.Normalize(record);
        Directory.CreateDirectory(_activeDirectory);

        lock (Sync)
        {
            File.AppendAllText(
                _canonicalPath,
                DbTextRepairModelJsonl.Serialize(record) + Environment.NewLine,
                new UTF8Encoding(false));
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
            _ready = true;
        }
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
