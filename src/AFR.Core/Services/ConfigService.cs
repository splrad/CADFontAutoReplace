using Microsoft.Win32;
using System.Text.RegularExpressions;
using AFR.Platform;

namespace AFR.Services;

/// <summary>
/// 业务配置服务，使用 Windows 注册表持久化保存插件设置。
/// <para>
/// 采用全局单例模式，通过 <see cref="Instance"/> 访问。
/// 提供带内存缓存的类型安全配置访问，并通过 <see cref="PlatformManager"/> 定位当前平台对应的注册表路径。
/// 写入时会同步到所有匹配的 CAD 版本注册表项。
/// </para>
/// </summary>
public sealed class ConfigService
{
    // 使用 Lazy<T> 实现线程安全的延迟初始化单例。
    private static readonly Lazy<ConfigService> _instance = new(() => new ConfigService());
    /// <summary>获取 ConfigService 的全局唯一实例。</summary>
    public static ConfigService Instance => _instance.Value;

    // 从 PlatformManager 获取当前 CAD 平台的注册表定位信息。
    private static string AutoCadBasePath => PlatformManager.Platform.RegistryBasePath;
    private static string AppName => PlatformManager.Platform.AppName;
    private static string KeyPattern => PlatformManager.Platform.RegistryKeyPattern;

    // 编译后的正则表达式，用于匹配当前 CAD 版本对应的子键名。
    private Regex? _keyPatternRegex;
    private Regex KeyPatternRegex => _keyPatternRegex ??= new Regex(KeyPattern, RegexOptions.Compiled);

    // ── 内存缓存字段：避免每次访问都读取注册表。──
    private string? _mainFont;
    private string? _bigFont;
    private string? _trueTypeFont;
    private int? _isInitialized;
    // volatile 确保多线程场景下可见最新状态。
    private volatile bool _cacheLoaded;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    // 缓存解析后的有效应用注册表路径。
    private List<string>? _resolvedAppPaths;

    // 私有构造函数：强制通过 Instance 访问。
    private ConfigService() { }

    /// <summary>
    /// 返回所有匹配当前 CAD 版本的应用程序注册表路径。
    /// <para>
    /// 遍历 CAD 根路径下的所有子键，筛选出与版本模式匹配的项，
    /// 再拼接为完整的 Applications\{AppName} 路径。结果会被缓存。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> GetAllApplicationPaths()
    {
        var cached = _resolvedAppPaths;
        if (cached != null) return cached;

        lock (_lock)
        {
            if (_resolvedAppPaths != null) return _resolvedAppPaths;

            var results = new List<string>();
            var subKeyNames = RegistryService.GetSubKeyNames(Registry.CurrentUser, AutoCadBasePath);
            foreach (var name in subKeyNames)
            {
                if (KeyPatternRegex.IsMatch(name))
                {
                    results.Add($@"{AutoCadBasePath}\{name}\Applications\{AppName}");
                }
            }

            _resolvedAppPaths = results;
            return results;
        }
    }

    /// <summary>
    /// 返回首个匹配的应用程序注册表路径，用于读写配置值。
    /// 若未找到匹配路径则返回 null。
    /// </summary>
    public string? GetPrimaryApplicationPath()
    {
        var paths = GetAllApplicationPaths();
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>
    /// 确保内存缓存已从注册表加载。
    /// 首次调用时读取全部配置值，后续调用直接复用缓存。
    /// </summary>
    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;
        lock (_lock)
        {
            if (_cacheLoaded) return;
            var path = GetPrimaryApplicationPath();
            if (path == null) return;

            _mainFont = RegistryService.ReadString(Registry.CurrentUser, path, "MainFont");
            _bigFont = RegistryService.ReadString(Registry.CurrentUser, path, "BigFont");
            _trueTypeFont = RegistryService.ReadString(Registry.CurrentUser, path, "TrueTypeFont");
            _isInitialized = RegistryService.ReadDword(Registry.CurrentUser, path, "IsInitialized");
            _cacheLoaded = true;
        }
    }

    /// <summary>
    /// SHX 主字体替换名称。
    /// 读取时优先使用缓存，写入时同步到所有匹配的注册表路径。
    /// </summary>
    public string MainFont
    {
        get
        {
            EnsureCacheLoaded();
            return _mainFont ?? string.Empty;
        }
        set
        {
            foreach (var path in GetAllApplicationPaths())
            {
                RegistryService.WriteString(Registry.CurrentUser, path, "MainFont", value);
            }
            lock (_lock) { _mainFont = value; }
        }
    }

    /// <summary>
    /// SHX 大字体替换名称。
    /// 读取时优先使用缓存，写入时同步到所有匹配的注册表路径。
    /// </summary>
    public string BigFont
    {
        get
        {
            EnsureCacheLoaded();
            return _bigFont ?? string.Empty;
        }
        set
        {
            foreach (var path in GetAllApplicationPaths())
            {
                RegistryService.WriteString(Registry.CurrentUser, path, "BigFont", value);
            }
            lock (_lock) { _bigFont = value; }
        }
    }

    /// <summary>
    /// TrueType 字体替换名称。
    /// 读取时优先使用缓存，写入时同步到所有匹配的注册表路径。
    /// </summary>
    public string TrueTypeFont
    {
        get
        {
            EnsureCacheLoaded();
            return _trueTypeFont ?? string.Empty;
        }
        set
        {
            foreach (var path in GetAllApplicationPaths())
            {
                RegistryService.WriteString(Registry.CurrentUser, path, "TrueTypeFont", value);
            }
            lock (_lock) { _trueTypeFont = value; }
        }
    }

    /// <summary>
    /// 插件是否已完成首次初始化配置。
    /// 注册表中以 DWORD 值存储（1 表示已初始化，0 表示未初始化）。
    /// </summary>
    public bool IsInitialized
    {
        get
        {
            EnsureCacheLoaded();
            return (_isInitialized ?? 0) == 1;
        }
        set
        {
            int val = value ? 1 : 0;
            foreach (var path in GetAllApplicationPaths())
            {
                RegistryService.WriteDword(Registry.CurrentUser, path, "IsInitialized", val);
            }
            lock (_lock) { _isInitialized = val; }
        }
    }

    /// <summary>
    /// 使内存缓存失效，下次访问配置属性时会重新读取注册表。
    /// 通常在外部修改注册表或需要强制刷新配置时调用。
    /// </summary>
    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cacheLoaded = false;
            _mainFont = null;
            _bigFont = null;
            _trueTypeFont = null;
            _isInitialized = null;
            _resolvedAppPaths = null;
        }
    }

    /// <summary>
    /// 删除所有匹配 CAD 版本注册表中的本插件应用键。
    /// 用于插件卸载时清理注册表。
    /// </summary>
    /// <returns>成功删除的注册表键数量。</returns>
    public int DeleteAllApplicationKeys()
    {
        int deletedCount = 0;
        if (string.IsNullOrWhiteSpace(AppName) || AppName.Contains('\\'))
            return deletedCount;

        var subKeyNames = RegistryService.GetSubKeyNames(Registry.CurrentUser, AutoCadBasePath);
        foreach (var name in subKeyNames)
        {
            if (!KeyPatternRegex.IsMatch(name)) continue;

            var appKeyPath = $@"{AutoCadBasePath}\{name}\Applications\{AppName}";

            if (!RegistryService.KeyExists(Registry.CurrentUser, appKeyPath)) continue;

            if (RegistryService.DeleteSubKeyTree(Registry.CurrentUser, appKeyPath))
            {
                deletedCount++;
            }
        }

        InvalidateCache();
        return deletedCount;
    }
}
