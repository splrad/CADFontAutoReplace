using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AFR.Deployer.Infrastructure;
using AFR.Deployer.Models;
using AFR.Deployer.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AFR.Deployer.ViewModels;

/// <summary>
/// 主窗口 ViewModel，协调扫描、安装、卸载、进程检测等业务逻辑。
/// </summary>
internal sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService                _dialog;
    private readonly IFolderPickerService          _folderPicker;
    private readonly List<RegistryChangeWatcher>   _registryWatchers = [];
    private readonly CadProcessWatcher             _processWatcher   = new();
    private readonly DispatcherTimer               _processPollTimer;

    /// <summary>
    /// 刷新节流定时器：把短时间内的注册表 / 进程事件合并为一次扫描，
    /// 避免频繁刷新挤占 Dispatcher 队列。
    /// </summary>
    private readonly DispatcherTimer               _refreshDebouncer;
    private static readonly TimeSpan               RefreshDebounceDelay = TimeSpan.FromMilliseconds(250);
    /// <summary>后台扫描互斥标记，避免事件风暴下重复排队。</summary>
    private int                                    _backgroundScanInFlight;
    private bool                                   _hasUpdate;
    private string                                 _latestVersion  = string.Empty;
    private string                                 _releasePageUrl = UpdateCheckService.ReleasesPageUrl;
    private UpdateCheckSource                      _updateSource   = UpdateCheckSource.GitHub;
    private const int                              MaxUpdateCheckAttempts = 3;

    [ObservableProperty]
    public partial string DeployPath { get; set; } = ResolveDefaultDeployPath();

    /// <summary>
    /// 选择默认部署根目录：优先使用首个非系统盘固定盘，
    /// 否则回落到系统盘下的同名目录，并保留末尾目录分隔符。
    /// </summary>
    private static string ResolveDefaultDeployPath()
    {
        const string FolderName = "CADPlugins";
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))
                         ?? @"C:\";

        try
        {
            var preferred = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .FirstOrDefault(root => !string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(preferred))
                return Path.Combine(preferred, FolderName) + Path.DirectorySeparatorChar;
        }
        catch
        {
            // 任何 IO 异常都回落到系统盘，保证 UI 始终有可用路径。
        }

        return Path.Combine(systemRoot, FolderName) + Path.DirectorySeparatorChar;
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "正在扫描已安装的 CAD……";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    public partial bool IsCadRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>已检测到（即本机已安装）的 CAD 版本数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectionSummary))]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    public partial int InstalledCount { get; set; }

    /// <summary>列表中条目总数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectionSummary))]
    public partial int TotalCount { get; set; }

    /// <summary>已部署且为最新版的 CAD 版本数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    public partial int DeployedCurrentCount { get; set; }

    /// <summary>已部署但版本陈旧的 CAD 版本数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    public partial int DeployedOutdatedCount { get; set; }

    /// <summary>注册表登记但 DLL 文件已丢失的 CAD 版本数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    public partial int DllMissingCount { get; set; }

    /// <summary>已检测到 CAD 但插件尚未部署的 CAD 版本数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    public partial int PendingCount { get; set; }

    /// <summary>顶部 chip 主行：检测到的 CAD 版本数 / 受支持版本总数。</summary>
    public string DetectionSummary => $"检测到 {InstalledCount} 个 CAD · 共支持 {TotalCount} 个版本";

    /// <summary>顶部 chip 副行：插件部署状态分布。</summary>
    public string PluginSummary
    {
        get
        {
            if (InstalledCount == 0) return string.Empty;

            var parts = new List<string>(4);
            if (DeployedCurrentCount  > 0) parts.Add($"最新 {DeployedCurrentCount}");
            if (DeployedOutdatedCount > 0) parts.Add($"旧版 {DeployedOutdatedCount}");
            if (DllMissingCount       > 0) parts.Add($"DLL 缺失 {DllMissingCount}");
            if (PendingCount          > 0) parts.Add($"待安装 {PendingCount}");
            return parts.Count == 0 ? string.Empty : "插件：" + string.Join(" · ", parts);
        }
    }

    /// <summary>是否需要显示插件部署副行。</summary>
    public bool HasPluginSummary => !string.IsNullOrEmpty(PluginSummary);

    /// <summary>部署工具自身的版本号，UI 显示用（X.Y 格式）。</summary>
    /// <remarks>
    /// 保持为实例属性：MainWindow.xaml 通过 <c>{Binding DeployerVersion}</c>
    /// 依赖 DataContext 解析，改成 static 会导致现有绑定失效。
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "WPF DataContext 绑定要求实例成员")]
    public string DeployerVersion => $"v{DeployerVersionService.GetDisplayVersion()}";

    /// <summary>是否检测到比当前部署器更新的正式发行版。</summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        set
        {
            if (!SetProperty(ref _hasUpdate, value)) return;
            OnPropertyChanged(nameof(VersionBadgeText));
            OnPropertyChanged(nameof(UpdateToolTip));
        }
    }

    /// <summary>GitHub 最新正式发行版版本号（X.Y）。</summary>
    public string LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (!SetProperty(ref _latestVersion, value)) return;
            OnPropertyChanged(nameof(VersionBadgeText));
            OnPropertyChanged(nameof(UpdateToolTip));
        }
    }

    /// <summary>点击更新提示时打开的发布页面。</summary>
    public string ReleasePageUrl
    {
        get => _releasePageUrl;
        set => SetProperty(ref _releasePageUrl, value);
    }

    /// <summary>检测到新版本的发布源。</summary>
    public UpdateCheckSource UpdateSource
    {
        get => _updateSource;
        set
        {
            if (!SetProperty(ref _updateSource, value)) return;
            OnPropertyChanged(nameof(UpdateToolTip));
        }
    }

    /// <summary>标题区版本徽章文本；发现新版本时作为更新入口。</summary>
    public string VersionBadgeText => HasUpdate && !string.IsNullOrWhiteSpace(LatestVersion)
        ? $"发现新版 v{LatestVersion}"
        : DeployerVersion;

    /// <summary>标题区版本徽章提示文本。</summary>
    public string UpdateToolTip => HasUpdate && !string.IsNullOrWhiteSpace(LatestVersion)
        ? $"当前版本 {DeployerVersion}，{GetUpdateSourceName(UpdateSource)} 最新版本 v{LatestVersion}。点击打开{GetUpdateSourceName(UpdateSource)}发行页下载。"
        : $"当前版本 {DeployerVersion}";

    /// <summary>操作按钮是否可用。</summary>
    public bool CanOperate => !IsCadRunning && !IsBusy;

    /// <summary>DataGrid 数据源。</summary>
    public ObservableCollection<CadEntryViewModel> CadEntries { get; } = [];

    internal MainViewModel(IDialogService dialog, IFolderPickerService folderPicker)
    {
        _dialog       = dialog;
        _folderPicker = folderPicker;

        // ── 进程实时监听（WMI）：尽快感知 CAD 启动与退出。
        _processWatcher.StateChanged += OnProcessChanged;
        _processWatcher.Start();

        // ── 兜底轮询：WMI 可能漏事件或不可用，2 秒轮询用于保证状态最终收敛。
        _processPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _processPollTimer.Tick += (_, _) => CheckCadProcesses();
        _processPollTimer.Start();

        // ── 刷新节流定时器：合并注册表 / 进程事件，并让输入与渲染优先处理。
        _refreshDebouncer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshDebounceDelay,
        };
        _refreshDebouncer.Tick += OnRefreshDebouncerTick;

        // ── 注册表实时监听：订阅各品牌根节点，覆盖版本与插件子键变更；
        //    若目标根尚不存在，则回退到上级节点等待其创建。
        var brandRoots = CadDescriptors.All
            .Select(d => GetBrandRoot(d.RegistryBasePath))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in brandRoots)
        {
            // fallbackRoot 取上一级，避免目标根尚未创建时监听失效。
            var fallback = GetParent(root) ?? root;
            var watcher  = new RegistryChangeWatcher(root, fallback);
            watcher.Changed += OnRegistryChanged;
            watcher.Start();
            _registryWatchers.Add(watcher);
        }

        Refresh();
        CheckCadProcesses();
        _ = CheckForUpdatesAsync();
    }

    /// <summary>取 RegistryBasePath 的品牌根，如 R25.0 → AutoCAD。</summary>
    private static string GetBrandRoot(string registryBasePath)
        => GetParent(registryBasePath) ?? registryBasePath;

    private static string? GetParent(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx > 0 ? path[..idx] : null;
    }

    /// <summary>
    /// 注册表事件回调（来自 ThreadPool 线程），通过节流定时器合并高频刷新请求。
    /// </summary>
    private void OnRegistryChanged() => ScheduleRefresh();

    /// <summary>
    /// 进程事件回调（来自 WMI 工作线程），同时更新运行状态并触发一次节流刷新。
    /// </summary>
    private void OnProcessChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke(CheckCadProcesses);
        ScheduleRefresh();
    }

    /// <summary>
    /// 把刷新请求送入节流定时器：首个事件启动计时，其余事件在窗口期内合并。
    /// 定时器只能在创建它的 UI 线程上启动或停止。
    /// </summary>
    private void ScheduleRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess())
            ArmThrottle();
        else
            dispatcher.BeginInvoke(ArmThrottle);
    }

    private void ArmThrottle()
    {
        if (IsBusy) return;                       // 安装 / 卸载进行中，不触发刷新。
        if (_refreshDebouncer.IsEnabled) return;  // 本轮事件已被合并。
        _refreshDebouncer.Start();
    }

    private void OnRefreshDebouncerTick(object? sender, EventArgs e)
    {
        _refreshDebouncer.Stop();
        if (IsBusy) return;

        // 只允许一个后台扫描同时运行；若已有扫描在跑，则等待下一次事件再触发。
        if (Interlocked.CompareExchange(ref _backgroundScanInFlight, 1, 0) != 0)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Interlocked.Exchange(ref _backgroundScanInFlight, 0);
            return;
        }

        // 扫描放到 ThreadPool，避免注册表读取阻塞 UI 线程。
        Task.Run(() =>
        {
            IReadOnlyList<CadInstallation> results;
            try
            {
                results = CadRegistryScanner.Scan();
            }
            catch
            {
                Interlocked.Exchange(ref _backgroundScanInFlight, 0);
                return;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!IsBusy) ApplyScanResults(results);
                }
                finally
                {
                    Interlocked.Exchange(ref _backgroundScanInFlight, 0);
                }
            }));
        });
    }

    // ── 扫描 ──

    /// <summary>
    /// 同步刷新入口，仅用于构造期和安装 / 卸载完成后这类需要立即收敛的场景。
    /// </summary>
    private void Refresh() => ApplyScanResults(CadRegistryScanner.Scan());

    /// <summary>把扫描结果应用到可观察集合并刷新汇总数。必须在 UI 线程调用。</summary>
    private void ApplyScanResults(IReadOnlyList<CadInstallation> results)
    {
        var toRemove = CadEntries
            .Where(e => !results.Any(r => IsSameEntry(r, e)))
            .ToList();
        foreach (var entry in toRemove)
            CadEntries.Remove(entry);

        foreach (var result in results)
        {
            var existing = CadEntries.FirstOrDefault(e => IsSameEntry(result, e));
            if (existing is not null)
                existing.Refresh(result);
            else
                CadEntries.Add(new CadEntryViewModel(result));
        }

        TotalCount             = CadEntries.Count;
        InstalledCount         = CadEntries.Count(e => e.IsCadInstalled);
        DeployedCurrentCount   = CadEntries.Count(e => e.IsCadInstalled && e.Status == PluginDeployStatus.InstalledCurrent);
        DeployedOutdatedCount  = CadEntries.Count(e => e.IsCadInstalled && e.Status == PluginDeployStatus.InstalledOutdated);
        DllMissingCount        = CadEntries.Count(e => e.IsCadInstalled && e.Status == PluginDeployStatus.DllMissing);
        PendingCount           = CadEntries.Count(e => e.IsCadInstalled && e.Status == PluginDeployStatus.NotInstalled);

        // 仅在空闲态覆盖状态文字，避免抹掉运行警告或操作结果。
        if (!IsCadRunning && !IsBusy &&
            (string.IsNullOrEmpty(StatusText) || StatusText is "就绪" or "正在扫描已安装的 CAD……"))
        {
            StatusText = CadEntries.Count == 0
                ? "未检测到任何受支持的 AutoCAD 安装"
                : "就绪";
        }
    }

    private static bool IsSameEntry(CadInstallation r, CadEntryViewModel e)
        => r.Descriptor.AppName == e.Installation.Descriptor.AppName
        && string.Equals(r.Descriptor.RegistryBasePath,
                         e.Installation.Descriptor.RegistryBasePath,
                         StringComparison.OrdinalIgnoreCase);

    // ── 进程检测 ──

    private void CheckCadProcesses()
    {
        var wasRunning = IsCadRunning;
        IsCadRunning   = ProcessGuardService.IsAnyCadRunning(out var names);

        if (IsCadRunning)
            StatusText = $"⚠ 检测到 CAD 正在运行（{string.Join("、", names)}），请关闭后再操作";
        else if (wasRunning)
            StatusText = "就绪";
    }

    // ── 路径浏览 ──

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var selected = await _folderPicker.PickFolderAsync(DeployPath);
        if (!string.IsNullOrEmpty(selected))
            DeployPath = selected;
    }

    // ── 更新检查 ──

    /// <summary>启动后静默检查 GitHub 最新发行版，不影响部署器主流程。</summary>
    private async Task CheckForUpdatesAsync()
    {
        var result = await CheckForUpdatesAsync(UpdateCheckSource.GitHub);
        if (!result.HasUpdate && !result.IsReachable)
            result = await CheckForUpdatesAsync(UpdateCheckSource.Gitee);

        if (!result.HasUpdate) return;

        LatestVersion  = result.LatestVersion;
        ReleasePageUrl = string.IsNullOrWhiteSpace(result.ReleaseUrl)
            ? UpdateCheckService.ReleasesPageUrl
            : result.ReleaseUrl;
        UpdateSource = result.Source;
        HasUpdate = true;
    }

    /// <summary>对指定发布源最多尝试三次；每次请求自身有硬超时，避免单次网络阻塞拖住后续检查。</summary>
    private static async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateCheckSource source)
    {
        for (var attempt = 1; attempt <= MaxUpdateCheckAttempts; attempt++)
        {
            var result = await UpdateCheckService.CheckAsync(source);
            if (result.HasUpdate) return result;
            if (result.IsReachable) return result;
            if (attempt < MaxUpdateCheckAttempts)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return UpdateCheckResult.Unreachable(source);
    }

    private static string GetUpdateSourceName(UpdateCheckSource source) => source switch
    {
        UpdateCheckSource.Gitee => "Gitee 镜像仓库",
        _ => "GitHub",
    };

    [RelayCommand]
    private async Task OpenReleasePageAsync()
    {
        var url = string.IsNullOrWhiteSpace(ReleasePageUrl)
            ? UpdateCheckService.ReleasesPageUrl
            : ReleasePageUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            await _dialog.ShowInfoAsync($"无法打开浏览器，请手动访问：\n{url}", "AFR 部署工具");
        }
    }

    // ── 安装 ──

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task InstallAsync()
    {
        var selected = CadEntries.Where(e => e.IsCadInstalled && e.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await _dialog.ShowInfoAsync("请先在列表中勾选要安装的 CAD 版本。", "AFR 部署工具");
            return;
        }

        var freshResults = CadRegistryScanner.Scan()
            .ToDictionary(r => r.Descriptor.AppName);

        IsBusy     = true;
        StatusText = "正在安装……";

        var errors    = new List<string>();
        var successes = 0;

        await Task.Run(() =>
        {
            // 收集安装成功的 CAD 版本，稍后统一处理 FixedProfile.aws。
            var patchedDescriptors = new HashSet<CadDescriptor>();

            foreach (var entry in selected)
            {
                var key = entry.Installation.Descriptor.AppName;
                var fresh = freshResults.GetValueOrDefault(key, entry.Installation);

                if (!PluginDeployer.TryInstall(fresh, DeployPath, out var err))
                    errors.Add($"{fresh.Descriptor.DisplayName}：{err}");
                else
                {
                    successes++;
                    patchedDescriptors.Add(fresh.Descriptor);

                    // 释放内嵌 SHX 字体到当前实例对应的 <AcadLocation>\Fonts。
                    // 失败仅记录警告，不阻断安装主流程。
                    try
                    {
                        if (!EmbeddedFontPatcher.Apply(fresh))
                            errors.Add($"{fresh.Descriptor.DisplayName}：默认 SHX 字体释放失败，请手动在CAD中运行AFR插件进行字体配置");
                    }
                    catch { /* 字体释放失败不影响安装主流程 */ }
                }
            }

            // 抑制“缺少 SHX 文件”对话框：在全部写入完成后统一处理 FixedProfile.aws。
            foreach (var desc in patchedDescriptors)
            {
                try { AwsHideableDialogPatcher.Apply(desc); }
                catch { /* 单个版本失败不影响其它版本；安装本身已成功 */ }
            }
        });

        IsBusy = false;
        Refresh();
        ClearSelection();

        if (errors.Count > 0)
            await _dialog.ShowWarningAsync(
                $"以下版本安装失败：\n\n{string.Join("\n", errors)}",
                "AFR 部署工具 — 安装错误");
        else
            StatusText = $"✓ 已成功安装 {successes} 个 CAD 版本并应用 SHX 缺失弹窗抑制，启动 CAD 时插件生效";
    }

    // ── 卸载 ──

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task UninstallAsync()
    {
        var selected = CadEntries
            .Where(e => e.IsCadInstalled && e.IsSelected && e.Status != PluginDeployStatus.NotInstalled)
            .ToList();

        if (selected.Count == 0)
        {
            await _dialog.ShowInfoAsync("请勾选已安装的 CAD 版本进行卸载。", "AFR 部署工具");
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            $"确定要从以下 {selected.Count} 个 CAD 版本中卸载 AFR 插件？\n\n" +
            string.Join("\n", selected.Select(e => $"  • {e.Installation.Descriptor.DisplayName}")),
            "AFR 部署工具 — 确认卸载");
        if (!confirmed) return;

        var freshResults = CadRegistryScanner.Scan()
            .ToDictionary(r => r.Descriptor.AppName);

        IsBusy     = true;
        StatusText = "正在卸载……";

        var warnings  = new List<string>();
        var successes = 0;

        await Task.Run(() =>
        {
            var patchedDescriptors = new HashSet<CadDescriptor>();

            foreach (var entry in selected)
            {
                var key = entry.Installation.Descriptor.AppName;
                var fresh = freshResults.GetValueOrDefault(key, entry.Installation);

                if (!PluginUninstaller.TryUninstall(fresh, out var warn))
                    warnings.Add($"{fresh.Descriptor.DisplayName}：{warn}");
                else
                {
                    if (warn is not null)
                        warnings.Add($"{fresh.Descriptor.DisplayName}（警告）：{warn}");
                    successes++;
                    patchedDescriptors.Add(fresh.Descriptor);
                }
            }

            // 清理本插件写入的 FixedProfile.aws 抑制节点。
            foreach (var desc in patchedDescriptors)
            {
                try { AwsHideableDialogPatcher.Cleanup(desc); }
                catch { /* 清理失败不影响卸载主流程 */ }
            }
        });

        IsBusy = false;
        Refresh();
        ClearSelection();

        if (warnings.Count > 0)
            await _dialog.ShowWarningAsync(string.Join("\n", warnings),
                "AFR 部署工具 — 卸载完成（含警告）");
        else
            StatusText = $"✓ 已成功卸载 {successes} 个 CAD 版本并还原由本插件写入的 SHX 缺失弹窗抑制设置";
    }

    /// <summary>清空所有条目的勾选状态，避免上一次操作的选择残留到下一次操作。</summary>
    private void ClearSelection()
    {
        foreach (var entry in CadEntries)
            entry.IsSelected = false;
    }
}
