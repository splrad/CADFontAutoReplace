using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using AFR.Platform;
using AFR.Services;

namespace AFR.Hosting;

internal static class AwsPatchAgentLauncher
{
    private const string AgentFileName = "AFR.AwsPatchAgent.exe";
    private const string AgentResourceName = "AFR.Resources.AFR.AwsPatchAgent.exe";

    public static bool TryStart(out string? errorMessage)
    {
        try
        {
            var agentPath = EnsureAgentExtracted();
            var currentProcessId = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var startInfo = new ProcessStartInfo
            {
                FileName = agentPath,
                Arguments = string.Join(
                    " ",
                    Quote("--pid"),
                    Quote(currentProcessId),
                    Quote("--brand"),
                    Quote("AutoCAD"),
                    Quote("--version"),
                    Quote(PlatformManager.Platform.VersionName),
                    Quote("--registry"),
                    Quote(PlatformManager.Platform.RegistryBasePath),
                    Quote("--app"),
                    Quote(PlatformManager.Platform.AppName),
                    Quote("--build"),
                    Quote(PluginVersionService.GetBuildId()),
                    Quote("--self-delete")),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = "agent 进程未启动";
                return false;
            }

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string EnsureAgentExtracted()
    {
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AFR",
            "AwsPatchAgent",
            SanitizePathSegment(PluginVersionService.GetBuildId()));
        Directory.CreateDirectory(targetDir);

        var targetPath = Path.Combine(targetDir, AgentFileName);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(AgentResourceName);
        if (stream is null)
        {
            if (File.Exists(targetPath)) return targetPath;
            throw new FileNotFoundException("未找到内嵌的 AWS 离线补写 agent。", AgentResourceName);
        }

        var tempPath = Path.Combine(targetDir, AgentFileName + ".tmp");
        using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.CopyTo(file);
        }

        try
        {
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);
        }
        catch (IOException)
        {
            try { File.Delete(tempPath); } catch { }
            if (!File.Exists(targetPath)) throw;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        return targetPath;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";
}
