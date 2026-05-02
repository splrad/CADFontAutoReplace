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
    private const string PluginVersionValueName = "PluginVersion";
    private const string PluginBuildIdValueName = "PluginBuildId";
    private const string ConfigSchemaVersionValueName = "ConfigSchemaVersion";

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
                DiagnosticLogger.Log("初始化", "未找到有效的 AutoCAD R25.1 配置文件 (ACAD-xxxx:xxx)");
                return false;
            }

            foreach (var profile in profiles)
            {
                var appPath = $@"{AutoCadBasePath}\{profile}\Applications\{AppName}";
                if (InitializeProfile(appPath, dllPath))
                    isFirstRun = true;
            }

            #if AFR_EXTERNAL_REGISTRY
            // 应用 [assembly: RegistryDefaultDwordAt(...)] 声明的外部默认值
            // （NETLOAD 直装路径与部署工具路径共用同一份声明，保证两条入口结果一致）。
            // 默认禁用：当前版本不写入任何外部注册表值。定义 AFR_EXTERNAL_REGISTRY 即可恢复。
            ExternalRegistryDefaultsApplier.Apply();
#endif

            // 抑制 AutoCAD“缺少 SHX 文件”弹窗：写入 FixedProfile.aws。
            // 仅在 AutoCAD 未运行时生效；NETLOAD 现场加载会被 Apply 内部进程检查拒绝。
            try { Diagnostics.AwsHideableDialogPatcher.Apply(); } catch { }
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
        string currentPluginVersion = PluginVersionService.GetDisplayVersion();
        string currentBuildId = PluginVersionService.GetBuildId();
        int currentConfigSchemaVersion = PluginVersionService.ConfigSchemaVersion;
        string? installedPluginVersion = RegistryService.ReadString(Registry.CurrentUser, appPath, PluginVersionValueName);
        string? installedBuildId = RegistryService.ReadString(Registry.CurrentUser, appPath, PluginBuildIdValueName);
        int? installedConfigSchemaVersion = RegistryService.ReadDword(Registry.CurrentUser, appPath, ConfigSchemaVersionValueName);

        // 自动加载键值（幂等写入 — 仅在值与预期不同时才写入注册表）
        WriteIfChanged(appPath, "LOADER", dllPath);
        WriteIfChanged(appPath, "LOADCTRLS", LoadCtrls);
        WriteIfChanged(appPath, "MANAGED", Managed);
        WriteIfChanged(appPath, "DESCRIPTION", Description);
        WriteIfChanged(appPath, PluginVersionValueName, currentPluginVersion);
        WriteIfChanged(appPath, PluginBuildIdValueName, currentBuildId);

        // 首次初始化时部署内嵌字体并写入默认配置
        if (isNewKey)
        {
            WriteDefaultConfiguration(appPath);
            WriteIfChanged(appPath, ConfigSchemaVersionValueName, currentConfigSchemaVersion);
        }
        else if (RegistryService.ReadDword(Registry.CurrentUser, appPath, "IsInitialized") != 1)
        {
            // 部署工具预创建注册表键时仅写入默认字体名 + IsInitialized=0，
            // 需要在插件首次加载时释放内嵌 SHX 到 CAD Fonts 目录并将 IsInitialized 翻为 1。
            CompleteDeployerInitialization(appPath);
            WriteIfChanged(appPath, ConfigSchemaVersionValueName, currentConfigSchemaVersion);
        }
        else if (installedConfigSchemaVersion != currentConfigSchemaVersion)
        {
            MigrateConfiguration(appPath, installedConfigSchemaVersion);
            WriteIfChanged(appPath, ConfigSchemaVersionValueName, currentConfigSchemaVersion);
            DiagnosticLogger.Log("初始化",
                $"配置版本已迁移: {installedConfigSchemaVersion?.ToString() ?? "未设置"} → {currentConfigSchemaVersion}");
        }
        else if (!string.Equals(installedPluginVersion, currentPluginVersion, StringComparison.Ordinal)
              || !string.Equals(installedBuildId, currentBuildId, StringComparison.Ordinal))
        {
            DiagnosticLogger.Log("初始化",
                $"插件版本已更新: {installedPluginVersion ?? "未设置"}+{installedBuildId ?? "未设置"} → {currentPluginVersion}+{currentBuildId}");
        }
        return isNewKey;
    }

    /// <summary>
    /// 完成由部署工具预创建注册表键时的剩余初始化。
    /// <para>
    /// 部署工具无法定位 acad.exe 的 Fonts 目录，因此只写入字体名称和 IsInitialized=0；
    /// 真正释放内嵌 SHX 文件由插件首次加载时执行，成功后将 IsInitialized 翻为 1。
    /// 若用户已自行覆盖默认字体名（值非空），则保留用户值。
    /// </para>
    /// </summary>
    private static void CompleteDeployerInitialization(string appPath)
    {
        bool deployed = EmbeddedFontDeployer.Deploy();

        // 部署工具创建键时已写入默认值，这里仅在缺失/为空时补齐，避免覆盖用户自定义。
        EnsureStringValue(appPath, "MainFont",     EmbeddedFontDeployer.DefaultMainFont);
        EnsureStringValue(appPath, "BigFont",      EmbeddedFontDeployer.DefaultBigFont);
        EnsureStringValue(appPath, "TrueTypeFont", EmbeddedFontDeployer.DefaultTrueTypeFont);

        RegistryService.WriteDword(Registry.CurrentUser, appPath, "IsInitialized", deployed ? 1 : 0);
        DiagnosticLogger.Log("初始化",
            deployed
                ? "部署工具预创建键 — 已释放内嵌字体并完成初始化"
                : "部署工具预创建键 — 字体释放失败，等待用户手动配置");
    }

    /// <summary>当注册表中字符串值缺失或为空白时写入默认值，否则保留用户已有值。</summary>
    private static void EnsureStringValue(string appPath, string name, string defaultValue)
    {
        var current = RegistryService.ReadString(Registry.CurrentUser, appPath, name);
        if (string.IsNullOrWhiteSpace(current))
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, name, defaultValue);
        }
    }

    /// <summary>写入首次安装时的默认配置。</summary>
    private static void WriteDefaultConfiguration(string appPath)
    {
        bool deployed = EmbeddedFontDeployer.Deploy();
        if (deployed)
        {
            // 字体部署成功：写入默认替换字体 + 已初始化标记，重启后即可自动工作
            RegistryService.WriteString(Registry.CurrentUser, appPath, "MainFont", EmbeddedFontDeployer.DefaultMainFont);
            RegistryService.WriteString(Registry.CurrentUser, appPath, "BigFont", EmbeddedFontDeployer.DefaultBigFont);
            RegistryService.WriteString(Registry.CurrentUser, appPath, "TrueTypeFont", EmbeddedFontDeployer.DefaultTrueTypeFont);
            RegistryService.WriteDword(Registry.CurrentUser, appPath, "IsInitialized", 1);
            DiagnosticLogger.Log("初始化", "首次安装 — 已部署默认字体并写入配置");
        }
        else
        {
            // 字体部署失败：回退为空值 + 未初始化，等待用户手动运行 AFR 命令
            RegistryService.WriteString(Registry.CurrentUser, appPath, "MainFont", string.Empty);
            RegistryService.WriteString(Registry.CurrentUser, appPath, "BigFont", string.Empty);
            RegistryService.WriteString(Registry.CurrentUser, appPath, "TrueTypeFont", string.Empty);
            RegistryService.WriteDword(Registry.CurrentUser, appPath, "IsInitialized", 0);
            DiagnosticLogger.Log("初始化", "首次安装 — 字体部署失败，等待用户手动配置");
        }
    }

    /// <summary>
    /// 按配置架构版本迁移注册表配置。
    /// <para>
    /// 迁移只补齐缺失值或替换已知旧默认值，不覆盖用户主动选择的自定义字体。
    /// </para>
    /// </summary>
    /// <param name="appPath">该配置文件对应的完整注册表路径。</param>
    /// <param name="installedConfigSchemaVersion">注册表中已有的配置架构版本。</param>
    private static void MigrateConfiguration(string appPath, int? installedConfigSchemaVersion)
    {
        EmbeddedFontDeployer.Deploy();

        MigrateDefaultString(appPath, "MainFont", EmbeddedFontDeployer.DefaultMainFont, "K_roms.shx");
        MigrateDefaultString(appPath, "BigFont", EmbeddedFontDeployer.DefaultBigFont);
        MigrateDefaultString(appPath, "TrueTypeFont", EmbeddedFontDeployer.DefaultTrueTypeFont);

        if (!RegistryService.ValueExists(Registry.CurrentUser, appPath, "IsInitialized"))
        {
            RegistryService.WriteDword(Registry.CurrentUser, appPath, "IsInitialized", 1);
        }
    }

    /// <summary>迁移单个字符串默认配置，保留用户自定义值。</summary>
    private static void MigrateDefaultString(string appPath, string name, string defaultValue, params string[] oldDefaultValues)
    {
        var current = RegistryService.ReadString(Registry.CurrentUser, appPath, name);
        if (string.IsNullOrWhiteSpace(current))
        {
            RegistryService.WriteString(Registry.CurrentUser, appPath, name, defaultValue);
            return;
        }

        for (int i = 0; i < oldDefaultValues.Length; i++)
        {
            if (string.Equals(current, oldDefaultValues[i], StringComparison.OrdinalIgnoreCase))
            {
                RegistryService.WriteString(Registry.CurrentUser, appPath, name, defaultValue);
                return;
            }
        }
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
