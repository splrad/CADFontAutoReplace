using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    [ObservableProperty]
    private string _deployPath = ResolveDefaultDeployPath();

    /// <summary>
    /// 选择默认部署根目录：优先使用首个非系统盘的固定盘（D:\、E:\…），
    /// 若全机仅有系统盘（典型笔记本只有 C 盘），则回落到系统盘下的同名目录。
    /// 末尾保留反斜杠，便于在文本框中直接拼接子目录。
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
            // 任何 IO 异常（权限、设备未就绪等）都回落到系统盘，保证 UI 始终有合法路径。
        }

        return Path.Combine(systemRoot, FolderName) + Path.DirectorySeparatorChar;
    }

    [ObservableProperty]
    private string _statusText = "正在扫描已安装的 CAD……";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool _isCadRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool _isBusy;

    /// <summary>已检测到（即本机已安装）的 CAD 版本数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectionSummary))]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    private int _installedCount;

    /// <summary>列表中条目总数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectionSummary))]
    private int _totalCount;

    /// <summary>已部署且为最新版的插件实例数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    private int _deployedCurrentCount;

    /// <summary>已部署但版本陈旧的插件实例数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    private int _deployedOutdatedCount;

    /// <summary>注册表登记但 DLL 文件已丢失的插件实例数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    private int _dllMissingCount;

    /// <summary>已检测到 CAD 但插件尚未部署的配置文件实例数量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginSummary))]
    [NotifyPropertyChangedFor(nameof(HasPluginSummary))]
    private int _pendingCount;

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
    public string DeployerVersion => $"v{DeployerVersionService.GetDisplayVersion()}";

    /// <summary>操作按钮是否可用。</summary>
    public bool CanOperate => !IsCadRunning && !IsBusy;

    /// <summary>DataGrid 数据源。</summary>
    public ObservableCollection<CadEntryViewModel> CadEntries { get; } = [];

    internal MainViewModel(IDialogService dialog, IFolderPickerService folderPicker)
    {
        _dialog       = dialog;
        _folderPicker = folderPicker;

        // ── 进程实时监听（WMI）：CAD 启动/退出立即触发，无需 DispatcherTimer 轮询。
        _processWatcher.StateChanged += OnProcessChanged;
        _processWatcher.Start();

        // ── 注册表实时监听：仅订阅各品牌"根"（如 Software\Autodesk\AutoCAD）的子树，
        // 既覆盖版本子键的创建/删除（CAD 安装/卸载），也覆盖 Applications\AFR-ACADxxxx
        // 的子键变更。即使根本身尚未存在，Watcher 会回退到更高祖先（HKCU\Software）等待
        // 其被创建后自动升级，不会漏掉首次安装事件。
        var brandRoots = CadDescriptors.All
            .Select(d => GetBrandRoot(d.RegistryBasePath))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in brandRoots)
        {
            // fallbackRoot 取上一级（如 Software\Autodesk），避免目标根尚未创建时
            // 监听器无键可监而失效；同时不至于宽到 HKCU\Software 整棵树。
            var fallback = GetParent(root) ?? root;
            var watcher  = new RegistryChangeWatcher(root, fallback);
            watcher.Changed += OnRegistryChanged;
            watcher.Start();
            _registryWatchers.Add(watcher);
        }

        Refresh();
        CheckCadProcesses();
    }

    /// <summary>取 RegistryBasePath 的品牌根，如 R25.0 → AutoCAD。</summary>
    private static string GetBrandRoot(string registryBasePath)
        => GetParent(registryBasePath) ?? registryBasePath;

    private static string? GetParent(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx > 0 ? path[..idx] : null;
    }

    private void OnRegistryChanged()
    {
        // 注册表事件来自 ThreadPool；UI 操作必须 Marshal 回 Dispatcher。
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!IsBusy) Refresh();
        });
    }

    private void OnProcessChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke(CheckCadProcesses);
    }

    // ── 扫描 ──

    private void Refresh()
    {
        var results = CadRegistryScanner.Scan();

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

        // 只在"未做任何操作"的中性态时才覆盖状态文字，避免抹掉 CAD 运行警告/安装结果。
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
        && r.ProfileSubKey      == e.Installation.ProfileSubKey;

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
            .ToDictionary(r => (r.Descriptor.AppName, r.ProfileSubKey));

        IsBusy     = true;
        StatusText = "正在安装……";

        var errors    = new List<string>();
        var successes = 0;

        await Task.Run(() =>
        {
            // 收集成功安装涉及的 CAD 版本（按 Descriptor 去重），
            // 在所有注册表/DLL 写入完成后统一处理 FixedProfile.aws，
            // 避免对同一 CAD 版本的多语言目录重复扫描。
            var patchedDescriptors = new HashSet<CadDescriptor>();

            foreach (var entry in selected)
            {
                var key = (entry.Installation.Descriptor.AppName, entry.Installation.ProfileSubKey);
                var fresh = freshResults.GetValueOrDefault(key, entry.Installation);

                if (!PluginDeployer.TryInstall(fresh, DeployPath, out var err))
                    errors.Add($"{fresh.Descriptor.DisplayName} [{fresh.ProfileSubKey}]：{err}");
                else
                {
                    successes++;
                    patchedDescriptors.Add(fresh.Descriptor);
                }
            }

            // 抑制 "缺少 SHX 文件" 对话框：必须在 CAD 已关闭时写入 FixedProfile.aws，
            // 这正是部署工具调用此方法的前置条件（CanOperate => !IsCadRunning）。
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
            StatusText = $"✓ 已成功安装 {successes} 个配置文件实例并应用 SHX 缺失对话框抑制，启动 CAD 时插件生效。";
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
            $"确定要从以下 {selected.Count} 个配置文件实例中卸载 AFR 插件？\n\n" +
            string.Join("\n", selected.Select(e => $"  • {e.Installation.Descriptor.DisplayName} [{e.Profile}]")),
            "AFR 部署工具 — 确认卸载");
        if (!confirmed) return;

        var freshResults = CadRegistryScanner.Scan()
            .ToDictionary(r => (r.Descriptor.AppName, r.ProfileSubKey));

        IsBusy     = true;
        StatusText = "正在卸载……";

        var warnings  = new List<string>();
        var successes = 0;

        await Task.Run(() =>
        {
            var patchedDescriptors = new HashSet<CadDescriptor>();

            foreach (var entry in selected)
            {
                var key = (entry.Installation.Descriptor.AppName, entry.Installation.ProfileSubKey);
                var fresh = freshResults.GetValueOrDefault(key, entry.Installation);

                if (!PluginUninstaller.TryUninstall(fresh, out var warn))
                    warnings.Add($"{fresh.Descriptor.DisplayName} [{fresh.ProfileSubKey}]：{warn}");
                else
                {
                    if (warn is not null)
                        warnings.Add($"{fresh.Descriptor.DisplayName} [{fresh.ProfileSubKey}]（警告）：{warn}");
                    successes++;
                    patchedDescriptors.Add(fresh.Descriptor);
                }
            }

            // 清理本插件写入的 FixedProfile.aws 抑制节点（用户手动设置的同名节点保留不动）。
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
            StatusText = $"✓ 已成功卸载 {successes} 个配置文件实例并还原由本插件写入的 SHX 缺失对话框抑制设置。";
    }

    /// <summary>清空所有条目的勾选状态，避免上一次操作的选择残留到下一次操作。</summary>
    private void ClearSelection()
    {
        foreach (var entry in CadEntries)
            entry.IsSelected = false;
    }
}
