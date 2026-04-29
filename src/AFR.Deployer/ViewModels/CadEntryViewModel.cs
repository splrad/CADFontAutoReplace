using AFR.Deployer.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AFR.Deployer.ViewModels;

/// <summary>
/// DataGrid 中单行的 ViewModel，对应本机一个 CAD 配置文件实例。
/// </summary>
internal sealed partial class CadEntryViewModel : ObservableObject
{
    /// <summary>对应的扫描结果（用于执行操作时定位注册表路径）。</summary>
    internal CadInstallation Installation { get; private set; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private PluginDeployStatus _status;

    [ObservableProperty]
    private string _installedVersion = "—";

    /// <summary>品牌名称，如 "AutoCAD"。</summary>
    public string Brand   => Installation.Descriptor.Brand;

    /// <summary>版本年份，如 "2025"。</summary>
    public string Version => Installation.Descriptor.Version;

    /// <summary>配置文件子键。</summary>
    public string Profile => Installation.ProfileSubKey;

    /// <summary>品牌首字母，用于徽标显示。</summary>
    public string BrandInitial => Brand.Length > 0 ? Brand[0].ToString().ToUpperInvariant() : "?";

    /// <summary>状态的中文显示文本。</summary>
    public string StatusText => Status switch
    {
        PluginDeployStatus.NotInstalled      => "未安装",
        PluginDeployStatus.InstalledCurrent  => "已安装（最新）",
        PluginDeployStatus.InstalledOutdated => "旧版本",
        PluginDeployStatus.DllMissing        => "DLL 缺失",
        _                                    => "未知"
    };

    internal CadEntryViewModel(CadInstallation installation)
    {
        Installation       = installation;
        _status            = installation.Status;
        _installedVersion  = installation.InstalledVersion ?? "—";
    }

    /// <summary>用新的扫描结果刷新本行数据。</summary>
    internal void Refresh(CadInstallation newInstallation)
    {
        Installation     = newInstallation;
        Status           = newInstallation.Status;
        InstalledVersion = newInstallation.InstalledVersion ?? "—";
    }
}
