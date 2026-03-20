using Microsoft.Win32;
using System.Reflection;
using System.Text.RegularExpressions;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.Core;

/// <summary>
/// 处理首次注册表初始化、自动加载键值设置、
/// LOADER 路径自修复以及默认配置创建。
/// 所有操作均为幂等的 — 不会重复写入。
/// </summary>
internal static class AppInitializer
{
    private const string AutoCadBasePath = ConfigService.AutoCadBasePath;
    private const string AppName = ConfigService.AppName;
    private const string Description = "AFR Auto Replace Font Plugin";
    private const int LoadCtrls = 2;   // 随 AutoCAD 启动自动加载
    private const int Managed = 1;     // 托管 .NET 插件

    private static readonly Regex AcadKeyPattern =
        new(@"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$", RegexOptions.Compiled);

    public static void Initialize()
    {
        var log = LogService.Instance;
        try
        {
            var dllPath = GetCurrentDllPath();
            log.Info($"插件 DLL 路径: {dllPath}");

            var profiles = GetAcadProfiles();
            if (profiles.Count == 0)
            {
                log.Warning("未找到有效的 AutoCAD R25.1 配置文件 (ACAD-xxxx:xxx)。");
                return;
            }

            foreach (var profile in profiles)
            {
                var appPath = $@"{AutoCadBasePath}\{profile}\Applications\{AppName}";
                InitializeProfile(appPath, dllPath, log);
            }

            log.Info("注册表初始化完成。");
        }
        catch (Exception ex)
        {
            log.Error("应用初始化失败", ex);
        }
    }

    private static void InitializeProfile(string appPath, string dllPath, LogService log)
    {
        bool isNewKey = !RegistryService.KeyExists(Registry.CurrentUser, appPath);

        // 自动加载键值（幂等 — 仅在值不同时写入）
        WriteIfChanged(appPath, "LOADER", dllPath, log);
        WriteIfChanged(appPath, "LOADCTRLS", LoadCtrls, log);
        WriteIfChanged(appPath, "MANAGED", Managed, log);
        WriteIfChanged(appPath, "DESCRIPTION", Description, log);

        // 首次初始化的默认配置
        if (isNewKey)
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, "MainFont", string.Empty);
            RegistryService.WriteString(Registry.CurrentUser, appPath, "BigFont", string.Empty);
            RegistryService.WriteDword(Registry.CurrentUser, appPath, "IsInitialized", 0);
            log.Info($"已写入默认配置: {appPath}");
        }

        // 路径自修复: 若 DLL 已移动，更新 LOADER
        RepairLoaderPath(appPath, dllPath, log);
    }

    private static void RepairLoaderPath(string appPath, string dllPath, LogService log)
    {
        var currentLoader = RegistryService.ReadString(Registry.CurrentUser, appPath, "LOADER");
        if (!string.Equals(currentLoader, dllPath, StringComparison.OrdinalIgnoreCase))
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, "LOADER", dllPath);
            log.Info($"LOADER 路径已修复: '{currentLoader}' → '{dllPath}'");
        }
    }

    private static void WriteIfChanged(string appPath, string name, string value, LogService log)
    {
        var current = RegistryService.ReadString(Registry.CurrentUser, appPath, name);
        if (!string.Equals(current, value, StringComparison.Ordinal))
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, name, value);
            log.Info($"注册表更新: {name} = {value}");
        }
    }

    private static void WriteIfChanged(string appPath, string name, int value, LogService log)
    {
        var current = RegistryService.ReadDword(Registry.CurrentUser, appPath, name);
        if (current != value)
        {
            RegistryService.WriteDword(Registry.CurrentUser, appPath, name, value);
            log.Info($"注册表更新: {name} = {value}");
        }
    }

    private static List<string> GetAcadProfiles()
    {
        var results = new List<string>();
        var subKeyNames = RegistryService.GetSubKeyNames(Registry.CurrentUser, AutoCadBasePath);
        foreach (var name in subKeyNames)
        {
            if (AcadKeyPattern.IsMatch(name))
            {
                results.Add(name);
            }
        }
        return results;
    }

    private static string GetCurrentDllPath()
    {
        return Assembly.GetExecutingAssembly().Location;
    }
}
