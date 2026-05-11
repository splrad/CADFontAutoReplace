using System;
using System.IO;
using System.Reflection;
using System.Text;
using AFR.DbTextRepairModel;

namespace AFR.Deployer.Services;

internal static class DbTextRepairModelDeploymentService
{
    internal static DbTextRepairModelMergeReport MergeAppDataModel()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CADFontAutoReplace");

        string embedded = ReadEmbeddedDataset();
        return DbTextRepairModelMergeEngine.MergeDirectory(
            directory,
            embedded,
            "deployer-embedded");
    }

    private static string ReadEmbeddedDataset()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(DbTextRepairModelConstants.ResourceName);
        if (stream == null)
            return string.Empty;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
