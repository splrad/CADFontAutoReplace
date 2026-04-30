using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private string _deployPath = @"D:\CADPlugins\";

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
    private int _installedCount;

    /// <summary>列表中条目总数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectionSummary))]
    private int _totalCount;

    /// <summary>底部状态栏左侧显示的检测摘要。</summary>
    public string DetectionSummary => $"已检测到 {InstalledCount} / {TotalCount} 版本";

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

        TotalCount     = CadEntries.Count;
        InstalledCount = CadEntries.Count(e => e.IsCadInstalled);

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

    // ── 全选 / 取消全选 ──

    [RelayCommand]
    private void SelectAll(string? parameter)
    {
        bool selectAll = parameter is null || parameter != "false";
        foreach (var entry in CadEntries)
        {
            if (entry.IsCadInstalled)
                entry.IsSelected = selectAll;
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
            .ToDictionary(r => (r.Descriptor.AppName, r.ProfileSubKey));

        IsBusy     = true;
        StatusText = "正在安装……";

        var errors    = new List<string>();
        var successes = 0;

        await Task.Run(() =>
        {
            foreach (var entry in selected)
            {
                var key = (entry.Installation.Descriptor.AppName, entry.Installation.ProfileSubKey);
                var fresh = freshResults.GetValueOrDefault(key, entry.Installation);

                if (!PluginDeployer.TryInstall(fresh, DeployPath, out var err))
                    errors.Add($"{fresh.Descriptor.DisplayName} [{fresh.ProfileSubKey}]：{err}");
                else
                    successes++;
            }
        });

        IsBusy = false;
        Refresh();

        if (errors.Count > 0)
            await _dialog.ShowWarningAsync(
                $"以下版本安装失败：\n\n{string.Join("\n", errors)}",
                "AFR 部署工具 — 安装错误");
        else
            StatusText = $"✓ 已成功安装 {successes} 个配置文件实例，重启对应 CAD 后生效。";
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
                }
            }
        });

        IsBusy = false;
        Refresh();

        if (warnings.Count > 0)
            await _dialog.ShowWarningAsync(string.Join("\n", warnings),
                "AFR 部署工具 — 卸载完成（含警告）");
        else
            StatusText = $"✓ 已成功卸载 {successes} 个配置文件实例。";
    }
}
