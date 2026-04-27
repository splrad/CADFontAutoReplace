using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AFR.Deployer.Infrastructure;
using AFR.Deployer.Models;
using AFR.Deployer.Services;

namespace AFR.Deployer.ViewModels;

/// <summary>
/// 主窗口 ViewModel，协调扫描、安装、卸载、进程检测等全部业务逻辑。
/// </summary>
internal sealed class MainViewModel : INotifyPropertyChanged
{
    // ── 进程轮询定时器（每 2 秒检测一次 CAD 是否正在运行） ──
    private readonly DispatcherTimer      _processTimer;
    private readonly IDialogService       _dialog;
    private readonly IFolderPickerService _folderPicker;

    private string _deployPath  = @"D:\CADPlugins\";
    private string _statusText  = "正在扫描已安装的 CAD……";
    private bool   _isCadRunning;
    private bool   _isBusy;

    // ── 公开属性 ──

    /// <summary>DLL 释放目标目录（绑定路径 TextBox）。</summary>
    public string DeployPath
    {
        get => _deployPath;
        set { _deployPath = value; OnPropertyChanged(); }
    }

    /// <summary>底部状态栏文本。</summary>
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>是否检测到 CAD 进程正在运行（控制操作按钮的可用性）。</summary>
    public bool IsCadRunning
    {
        get => _isCadRunning;
        private set { _isCadRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanOperate)); }
    }

    /// <summary>是否正在执行安装/卸载操作（防重入）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanOperate)); }
    }

    /// <summary>操作按钮是否可用（无 CAD 运行且无后台任务）。</summary>
    public bool CanOperate => !IsCadRunning && !IsBusy;

    /// <summary>DataGrid 数据源：本机所有 CAD 配置文件实例。</summary>
    public ObservableCollection<CadEntryViewModel> CadEntries { get; } = [];

    // ── 命令 ──

    /// <summary>刷新扫描结果命令。</summary>
    public RelayCommand RefreshCommand  { get; }

    /// <summary>浏览目标目录命令。</summary>
    public RelayCommand BrowseCommand   { get; }

    /// <summary>安装所有勾选项命令。</summary>
    public RelayCommand InstallCommand  { get; }

    /// <summary>卸载所有勾选项命令。</summary>
    public RelayCommand UninstallCommand { get; }

    /// <summary>全选 / 取消全选命令。</summary>
    public RelayCommand SelectAllCommand { get; }

    internal MainViewModel(IDialogService dialog, IFolderPickerService folderPicker)
    {
        _dialog       = dialog;
        _folderPicker = folderPicker;

        RefreshCommand   = new RelayCommand(_ => Refresh());
        BrowseCommand    = new RelayCommand(_ => BrowsePath());
        InstallCommand   = new RelayCommand(_ => ExecuteInstall(),   _ => CanOperate);
        UninstallCommand = new RelayCommand(_ => ExecuteUninstall(), _ => CanOperate);
        SelectAllCommand = new RelayCommand(p  => ToggleSelectAll(p));

        // 启动进程轮询定时器
        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _processTimer.Tick += (_, _) => CheckCadProcesses();
        _processTimer.Start();

        // 初始扫描
        Refresh();
    }

    // ── 扫描 ──

    /// <summary>重新读取注册表，刷新 DataGrid 数据。</summary>
    private void Refresh()
    {
        StatusText = "正在扫描……";
        var results = CadRegistryScanner.Scan();

        // 更新已有行 / 追加新行 / 移除消失的行（按 Brand+Version+Profile 匹配）
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

        StatusText = CadEntries.Count == 0
            ? "未检测到任何受支持的 AutoCAD 安装"
            : $"共检测到 {CadEntries.Count} 个配置文件实例";

        CheckCadProcesses();
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

    private void BrowsePath()
    {
        var selected = _folderPicker.PickFolder(DeployPath);
        if (selected is not null)
            DeployPath = selected;
    }

    // ── 安装 ──

    private void ExecuteInstall()
    {
        var selected = CadEntries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _dialog.ShowInfo("请先在列表中勾选要安装的 CAD 版本。",
                "AFR 部署工具");
            return;
        }

        // 操作前再次读取注册表（防手动修改）
        var freshResults = CadRegistryScanner.Scan()
            .ToDictionary(r => (r.Descriptor.AppName, r.ProfileSubKey));

        IsBusy = true;
        StatusText = "正在安装……";

        var errors   = new List<string>();
        var successes = 0;

        foreach (var entry in selected)
        {
            // 使用最新扫描结果
            var key = (entry.Installation.Descriptor.AppName, entry.Installation.ProfileSubKey);
            var freshInstallation = freshResults.GetValueOrDefault(key, entry.Installation);

            if (!PluginDeployer.TryInstall(freshInstallation, DeployPath, out var err))
                errors.Add($"{freshInstallation.Descriptor.DisplayName} [{freshInstallation.ProfileSubKey}]：{err}");
            else
                successes++;
        }

        IsBusy = false;
        Refresh();

        if (errors.Count > 0)
        {
            var msg = $"以下版本安装失败：\n\n{string.Join("\n", errors)}";
            _dialog.ShowWarning(msg, "AFR 部署工具 — 安装错误");
        }
        else
        {
            StatusText = $"✓ 已成功安装 {successes} 个配置文件实例，重启对应 CAD 后生效。";
        }
    }

    // ── 卸载 ──

    private void ExecuteUninstall()
    {
        var selected = CadEntries
            .Where(e => e.IsSelected && e.Status != PluginDeployStatus.NotInstalled)
            .ToList();

        if (selected.Count == 0)
        {
            _dialog.ShowInfo("请勾选已安装的 CAD 版本进行卸载。",
                "AFR 部署工具");
            return;
        }

        if (!_dialog.Confirm(
            $"确定要从以下 {selected.Count} 个配置文件实例中卸载 AFR 插件？\n\n" +
            string.Join("\n", selected.Select(e => $"  • {e.Installation.Descriptor.DisplayName} [{e.Profile}]")),
            "AFR 部署工具 — 确认卸载")) return;

        // 操作前再次读取注册表（防手动修改）
        var freshResults = CadRegistryScanner.Scan()
            .ToDictionary(r => (r.Descriptor.AppName, r.ProfileSubKey));

        IsBusy = true;
        StatusText = "正在卸载……";

        var warnings = new List<string>();
        var successes = 0;

        foreach (var entry in selected)
        {
            var key = (entry.Installation.Descriptor.AppName, entry.Installation.ProfileSubKey);
            var freshInstallation = freshResults.GetValueOrDefault(key, entry.Installation);

            if (!PluginUninstaller.TryUninstall(freshInstallation, out var warn))
                warnings.Add($"{freshInstallation.Descriptor.DisplayName} [{freshInstallation.ProfileSubKey}]：{warn}");
            else
            {
                if (warn is not null)
                    warnings.Add($"{freshInstallation.Descriptor.DisplayName} [{freshInstallation.ProfileSubKey}]（警告）：{warn}");
                successes++;
            }
        }

        IsBusy = false;
        Refresh();

        if (warnings.Count > 0)
        {
            _dialog.ShowWarning(string.Join("\n", warnings),
                "AFR 部署工具 — 卸载完成（含警告）");
        }
        else
        {
            StatusText = $"✓ 已成功卸载 {successes} 个配置文件实例。";
        }
    }

    // ── 全选 / 取消全选 ──

    private void ToggleSelectAll(object? parameter)
    {
        bool selectAll = parameter is not string s || s != "false";
        foreach (var entry in CadEntries)
            entry.IsSelected = selectAll;
    }

    // ── INotifyPropertyChanged ──

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
