using System.ComponentModel;
using System.Runtime.CompilerServices;
using AFR.Deployer.Models;

namespace AFR.Deployer.ViewModels;

/// <summary>
/// DataGrid 中单行的 ViewModel，对应本机一个 CAD 配置文件实例。
/// </summary>
public sealed class CadEntryViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private PluginDeployStatus _status;

    /// <summary>对应的扫描结果（只读，用于执行操作时定位注册表路径）。</summary>
    internal CadInstallation Installation { get; private set; }

    /// <summary>用户是否勾选该条目（绑定 DataGrid CheckBox 列）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    // ── 展示用属性（直接映射自 Installation） ──

    /// <summary>品牌名称，如 "AutoCAD"。</summary>
    public string Brand => Installation.Descriptor.Brand;

    /// <summary>版本年份，如 "2025"。</summary>
    public string Version => Installation.Descriptor.Version;

    /// <summary>配置文件子键，如 "ACAD-12345:409"。</summary>
    public string Profile => Installation.ProfileSubKey;

    /// <summary>已安装的插件版本号，未安装时显示 "—"。</summary>
    public string InstalledVersion => Installation.InstalledVersion ?? "—";

    /// <summary>插件部署状态。</summary>
    public PluginDeployStatus Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    /// <summary>状态的中文显示文本。</summary>
    public string StatusText => Status switch
    {
        PluginDeployStatus.NotInstalled      => "未安装",
        PluginDeployStatus.InstalledCurrent  => "✓ 已安装（最新）",
        PluginDeployStatus.InstalledOutdated => "⚠ 已安装（旧版）",
        PluginDeployStatus.DllMissing        => "✗ DLL 文件缺失",
        _                                    => "未知"
    };

    internal CadEntryViewModel(CadInstallation installation)
    {
        Installation = installation;
        _status      = installation.Status;
    }

    /// <summary>用新的扫描结果刷新本行数据（操作完成后调用）。</summary>
    internal void Refresh(CadInstallation newInstallation)
    {
        Installation = newInstallation;
        Status       = newInstallation.Status;
        OnPropertyChanged(nameof(InstalledVersion));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
