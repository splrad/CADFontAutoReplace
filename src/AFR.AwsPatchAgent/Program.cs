using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AFR.HostIntegration;
using Microsoft.Win32;

namespace AFR.AwsPatchAgent;

internal static class Program
{
    private const string PendingAwsOverrideValueName = "PendingAwsOverride";
    private const string PendingAwsOverrideBuildIdValueName = "PendingAwsOverrideBuildId";
    private const string PendingAwsOverrideReasonValueName = "PendingAwsOverrideReason";

    private static readonly string[] WatchedProcessNames = ["acad"];
    private static readonly Regex ProfilePattern = new(@"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$", RegexOptions.Compiled);

    private static int Main(string[] args)
    {
        if (!Arguments.TryParse(args, out var options))
            return 64;

        using var mutex = new Mutex(
            initiallyOwned: true,
            name: BuildMutexName(options),
            createdNew: out var createdNew);
        if (!createdNew)
            return 0;

        try
        {
            WaitForTargetProcessExit(options.Pid);

            var result = TryPatchWhenCadIsClosed(options);
            if (result)
            {
                ClearPending(options);
                if (options.SelfDelete)
                    ScheduleSelfDelete();
                return 0;
            }

            return 2;
        }
        catch
        {
            return 1;
        }
    }

    private static bool TryPatchWhenCadIsClosed(Arguments options)
    {
        while (true)
        {
            WaitUntilNoCadProcess();

            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (IsAnyCadProcessRunning())
                    break;

                var state = AwsHideableDialogPatcherCore.GetSuppressionState(
                    options.Brand,
                    options.Version,
                    options.RegistryBasePath);
                if (state == AwsDialogSuppressionState.Correct)
                    return true;

                AwsHideableDialogPatcherCore.ApplyInstallOrUpdateOverride(
                    options.Brand,
                    options.Version,
                    options.RegistryBasePath);

                state = AwsHideableDialogPatcherCore.GetSuppressionState(
                    options.Brand,
                    options.Version,
                    options.RegistryBasePath);
                if (state == AwsDialogSuppressionState.Correct)
                    return true;

                Thread.Sleep(200);
            }

            if (!IsAnyCadProcessRunning())
                return false;
        }
    }

    private static void WaitForTargetProcessExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit();
        }
        catch
        {
        }
    }

    private static void WaitUntilNoCadProcess()
    {
        while (IsAnyCadProcessRunning())
            Thread.Sleep(500);
    }

    private static bool IsAnyCadProcessRunning()
    {
        foreach (var name in WatchedProcessNames)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearPending(Arguments options)
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(options.RegistryBasePath, writable: false);
            if (baseKey is null) return;

            foreach (var profile in baseKey.GetSubKeyNames().Where(n => ProfilePattern.IsMatch(n)))
            {
                var appPath = $@"{options.RegistryBasePath}\{profile}\Applications\{options.AppName}";
                try
                {
                    using var appKey = Registry.CurrentUser.OpenSubKey(appPath, writable: true);
                    if (appKey is null) continue;

                    appKey.DeleteValue(PendingAwsOverrideValueName, throwOnMissingValue: false);
                    appKey.DeleteValue(PendingAwsOverrideBuildIdValueName, throwOnMissingValue: false);
                    appKey.DeleteValue(PendingAwsOverrideReasonValueName, throwOnMissingValue: false);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static string BuildMutexName(Arguments options)
    {
        var raw = $"AFR.AwsPatchAgent.{options.AppName}.{options.BuildId}";
        var chars = raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return @"Local\" + new string(chars);
    }

    private static void ScheduleSelfDelete()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath)) return;
            var currentExePath = exePath!;

            var dir = Path.GetDirectoryName(currentExePath);
            var command = "/c ping 127.0.0.1 -n 2 > nul"
                        + " & del /f /q " + QuoteForCmd(currentExePath);
            if (!string.IsNullOrWhiteSpace(dir))
                command += " & rd " + QuoteForCmd(dir!) + " 2> nul";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
        }
    }

    private static string QuoteForCmd(string value)
        => "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed class Arguments
    {
        public int Pid { get; private set; }
        public string Brand { get; private set; } = string.Empty;
        public string Version { get; private set; } = string.Empty;
        public string RegistryBasePath { get; private set; } = string.Empty;
        public string AppName { get; private set; } = string.Empty;
        public string BuildId { get; private set; } = string.Empty;
        public bool SelfDelete { get; private set; }

        public static bool TryParse(string[] args, out Arguments options)
        {
            options = new Arguments();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--self-delete", StringComparison.OrdinalIgnoreCase))
                {
                    options.SelfDelete = true;
                    continue;
                }

                if (!arg.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                    return false;

                map[arg] = args[++i];
            }

            if (!map.TryGetValue("--pid", out var pidText)
                || !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                return false;

            options.Pid = pid;
            options.Brand = ReadRequired(map, "--brand");
            options.Version = ReadRequired(map, "--version");
            options.RegistryBasePath = ReadRequired(map, "--registry");
            options.AppName = ReadRequired(map, "--app");
            options.BuildId = map.TryGetValue("--build", out var build) ? build : string.Empty;

            return !string.IsNullOrWhiteSpace(options.Brand)
                && !string.IsNullOrWhiteSpace(options.Version)
                && !string.IsNullOrWhiteSpace(options.RegistryBasePath)
                && !string.IsNullOrWhiteSpace(options.AppName);
        }

        private static string ReadRequired(Dictionary<string, string> map, string name)
            => map.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
