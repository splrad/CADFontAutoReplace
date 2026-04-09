using Microsoft.Win32;
using System.Reflection;
using System.Text.RegularExpressions;
using AFR.Platform;
using AFR.Services;

namespace AFR.Hosting;

/// <summary>
/// 处理插件的首次注册表初始化、自动加载键值设置以及默认配置创建。
/// <para>
/// 在 AutoCAD 注册表中为每个匹配的 CAD 配置文件创建自动加载条目，
/// 使插件在 CAD 启动时自动加载。所有写入操作均为幂等的，不会重复覆盖已有值。
/// </para>
/// </summary>
internal static class AppInitializer
{
    // 从 PlatformManager 获取当前平台的注册表路径信息
    private static string AutoCadBasePath => PlatformManager.Platform.RegistryBasePath;
    private static string AppName => PlatformManager.Platform.AppName;

    // 注册表中自动加载条目的固定值
    private const string Description = "AFR Auto Replace Font Plugin";
    private const int LoadCtrls = 2;   // 2 = 随 AutoCAD 启动自动加载
    private const int Managed = 1;     // 1 = 标识为托管 .NET 插件

    /// <summary>
    /// 执行注册表初始化：为所有匹配的 CAD 配置文件创建/更新自动加载条目。
    /// </summary>
    /// <returns>true 表示首次安装（至少一个配置文件是新建的），false 表示更新已有配置。</returns>
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
                DiagnosticLogger.Log("初始化", $"未找到有效的 AutoCAD 配置文件 (ACAD-xxxx:xxx)，注册表路径: {PlatformManager.Platform.RegistryBasePath}");
                return false;
            }

            foreach (var profile in profiles)
            {
                var appPath = $@"{AutoCadBasePath}\{profile}\Applications\{AppName}";
                if (InitializeProfile(appPath, dllPath))
                    isFirstRun = true;
            }
        }
        catch (Exception ex)
        {
            log.Error("初始化失败", ex);
        }
        return isFirstRun;
    }

    /// <summary>
    /// 初始化单个 CAD 配置文件的注册表项。
    /// 写入自动加载所需的键值（LOADER、LOADCTRLS 等），首次创建时还会写入默认配置。
    /// </summary>
    /// <param name="appPath">该配置文件对应的完整注册表路径。</param>
    /// <param name="dllPath">插件 DLL 的完整文件路径。</param>
    /// <returns>true 表示是首次创建（之前不存在该注册表键）。</returns>
    private static bool InitializeProfile(string appPath, string dllPath)
    {
        bool isNewKey = !RegistryService.KeyExists(Registry.CurrentUser, appPath);

        // 自动加载键值（幂等写入 — 仅在值与预期不同时才写入注册表）
        WriteIfChanged(appPath, "LOADER", dllPath);
        WriteIfChanged(appPath, "LOADCTRLS", LoadCtrls);
        WriteIfChanged(appPath, "MANAGED", Managed);
        WriteIfChanged(appPath, "DESCRIPTION", Description);

        // 首次初始化时写入默认配置（空字体名 + 未初始化标记），等待用户通过 AFR 命令配置
        if (isNewKey)
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, "MainFont", string.Empty);
            RegistryService.WriteString(Registry.CurrentUser, appPath, "BigFont", string.Empty);
            RegistryService.WriteDword(Registry.CurrentUser, appPath, "IsInitialized", 0);
            DiagnosticLogger.Log("初始化", "首次安装 — 已写入默认配置");
        }
        return isNewKey;
    }

    /// <summary>仅在注册表中的当前值与目标值不同时才写入（字符串版本）。</summary>
    private static void WriteIfChanged(string appPath, string name, string value)
    {
        var current = RegistryService.ReadString(Registry.CurrentUser, appPath, name);
        if (!string.Equals(current, value, StringComparison.Ordinal))
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, name, value);
        }
    }

    /// <summary>仅在注册表中的当前值与目标值不同时才写入（DWORD 版本）。</summary>
    private static void WriteIfChanged(string appPath, string name, int value)
    {
        var current = RegistryService.ReadDword(Registry.CurrentUser, appPath, name);
        if (current != value)
        {
            RegistryService.WriteDword(Registry.CurrentUser, appPath, name, value);
        }
    }

    /// <summary>
    /// 枚举注册表中与当前 CAD 版本匹配的所有配置文件子键名。
    /// </summary>
    private static List<string> GetAcadProfiles()
    {
        var results = new List<string>();
        var pattern = new Regex(PlatformManager.Platform.RegistryKeyPattern, RegexOptions.Compiled);
        var subKeyNames = RegistryService.GetSubKeyNames(Registry.CurrentUser, AutoCadBasePath);
        foreach (var name in subKeyNames)
        {
            if (pattern.IsMatch(name))
            {
                results.Add(name);
            }
        }
        return results;
    }

    /// <summary>获取当前正在执行的插件 DLL 的完整文件路径。</summary>
    private static string GetCurrentDllPath()
    {
        return Assembly.GetExecutingAssembly().Location;
    }
}
