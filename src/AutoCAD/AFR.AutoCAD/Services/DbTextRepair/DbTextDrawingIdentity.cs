using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace AFR.Services.DbTextRepair;

internal sealed class DbTextDrawingIdentity
{
    public string Path { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long Length { get; init; }
    public string LastWriteUtc { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;

    public static DbTextDrawingIdentity FromDatabase(Database db)
    {
        string path = db.Filename ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return new DbTextDrawingIdentity();

        try
        {
            var info = new FileInfo(path);
            return new DbTextDrawingIdentity
            {
                Path = path,
                FileName = info.Name,
                Length = info.Exists ? info.Length : 0,
                LastWriteUtc = info.Exists ? info.LastWriteTimeUtc.ToString("O") : string.Empty,
                Sha256 = ComputeSha256(path)
            };
        }
        catch
        {
            return new DbTextDrawingIdentity
            {
                Path = path,
                FileName = SafeFileName(path)
            };
        }
    }

    private static string ComputeSha256(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                builder.Append(hash[i].ToString("X2"));
            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeFileName(string path)
    {
        try { return System.IO.Path.GetFileName(path); }
        catch { return string.Empty; }
    }
}
