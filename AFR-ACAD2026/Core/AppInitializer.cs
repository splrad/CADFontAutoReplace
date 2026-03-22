using Microsoft.Win32;
using System.Reflection;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.Core;

/// <summary>
/// 处理首次注册表初始化、自动加载键值设置以及默认配置创建。
/// 所有操作均为幂等的 — 不会重复写入。
/// </summary>
internal static class AppInitializer
{
    private const string AutoCadBasePath = ConfigService.AutoCadBasePath;
    private const string AppName = ConfigService.AppName;
    private const string Description = "AFR Auto Replace Font Plugin";
    private const int LoadCtrls = 2;   // 随 AutoCAD 启动自动加载
    private const int Managed = 1;     // 托管 .NET 插件

    /// <summary>
    /// 执行注册表初始化。返回 true 表示首次安装（新建了注册表项）。
    /// </summary>
    public static bool Initialize()
    {
        var log = LogService.Instance;
        bool isFirstRun = false;
        try
        {
            var dllPath = GetCurrentDllPath();

            var profiles = GetAcadProfiles();
            if (profiles.Count == 0)
            {
                log.Warning("未找到有效的 AutoCAD R25.1 配置文件 (ACAD-xxxx:xxx)。");
                return false;
            }

            foreach (var profile in profiles)
            {
                var appPath = $@"{AutoCadBasePath}\{profile}\Applications\{AppName}";
                if (InitializeProfile(appPath, dllPath, log))
                    isFirstRun = true;
            }
        }
        catch (Exception ex)
        {
            log.Error("应用初始化失败", ex);
        }
        return isFirstRun;
    }

    /// <summary>
    /// 初始化单个配置文件。返回 true 表示首次创建。
    /// </summary>
    private static bool InitializeProfile(string appPath, string dllPath, LogService log)
    {
        bool isNewKey = !RegistryService.KeyExists(Registry.CurrentUser, appPath);

        // 自动加载键值（幂等 — 仅在值不同时写入）
        WriteIfChanged(appPath, "LOADER", dllPath);
        WriteIfChanged(appPath, "LOADCTRLS", LoadCtrls);
        WriteIfChanged(appPath, "MANAGED", Managed);
        WriteIfChanged(appPath, "DESCRIPTION", Description);

        // 首次初始化的默认配置
        if (isNewKey)
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, "MainFont", string.Empty);
            RegistryService.WriteString(Registry.CurrentUser, appPath, "BigFont", string.Empty);
            RegistryService.WriteDword(Registry.CurrentUser, appPath, "IsInitialized", 0);
            log.Info($"首次安装 — 已写入默认配置。");
        }
        return isNewKey;
    }

    private static void WriteIfChanged(string appPath, string name, string value)
    {
        var current = RegistryService.ReadString(Registry.CurrentUser, appPath, name);
        if (!string.Equals(current, value, StringComparison.Ordinal))
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, name, value);
        }
    }

    private static void WriteIfChanged(string appPath, string name, int value)
    {
        var current = RegistryService.ReadDword(Registry.CurrentUser, appPath, name);
        if (current != value)
        {
            RegistryService.WriteDword(Registry.CurrentUser, appPath, name, value);
        }
    }

    private static List<string> GetAcadProfiles()
    {
        var results = new List<string>();
        var subKeyNames = RegistryService.GetSubKeyNames(Registry.CurrentUser, AutoCadBasePath);
        foreach (var name in subKeyNames)
        {
            if (ConfigService.AcadKeyPatternRegex().IsMatch(name))
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
