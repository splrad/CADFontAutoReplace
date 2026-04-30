using AFR.Deployer.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AFR.Deployer.ViewModels;

/// <summary>
/// CAD 列表中单个块的 ViewModel，对应一个受支持的 CAD 配置文件实例
/// 或一条“未安装”的占位条目。
/// </summary>
internal sealed partial class CadEntryViewModel : ObservableObject
{
    /// <summary>对应的扫描结果（用于执行操作时定位注册表路径）。</summary>
    internal CadInstallation Installation { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    [NotifyPropertyChangedFor(nameof(SubText))]
    [NotifyPropertyChangedFor(nameof(ShowSubText))]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    [NotifyPropertyChangedFor(nameof(ShowVersionRow))]
    private PluginDeployStatus _status;

    [ObservableProperty]
    private string _installedVersion = "—";

    /// <summary>本机是否已安装该 CAD 版本。未安装的占位条目在 UI 中应被禁用。</summary>
    public bool IsCadInstalled => Installation.IsCadInstalled;

    /// <summary>UI 是否允许勾选/操作此条目。</summary>
    public bool IsEnabled => Installation.IsCadInstalled;

    /// <summary>是否可对此条目执行卸载（必须已安装且当前部署不是“未安装”状态）。</summary>
    public bool CanUninstall => IsCadInstalled && Status != PluginDeployStatus.NotInstalled;

    /// <summary>品牌名称，如 "AutoCAD"。</summary>
    public string Brand   => Installation.Descriptor.Brand;

    /// <summary>版本年份，如 "2025"。</summary>
    public string Version => Installation.Descriptor.Version;

    /// <summary>配置文件子键；未安装时显示固定提示。</summary>
    public string Profile => Installation.IsCadInstalled
        ? Installation.ProfileSubKey
        : "未安装";

    /// <summary>显示名称，如 "AutoCAD 2025"。</summary>
    public string DisplayName => Installation.Descriptor.DisplayName;

    /// <summary>品牌首字母，用于徽标显示。</summary>
    public string BrandInitial => Brand.Length > 0 ? Brand[0].ToString().ToUpperInvariant() : "?";

    /// <summary>状态的中文显示文本。</summary>
    public string StatusText => !IsCadInstalled
        ? "未检测到 CAD"
        : Status switch
        {
            PluginDeployStatus.NotInstalled      => "待安装",
            PluginDeployStatus.InstalledCurrent  => "已安装",
            PluginDeployStatus.InstalledOutdated => "已安装",
            PluginDeployStatus.DllMissing        => "DLL 缺失",
            _                                    => "未知"
        };

    /// <summary>
    /// 卡片副文本：对状态做一句话补充说明。仅在状态为"未安装"时显示，
    /// 其他状态由版本号 + 徽章承载信息，避免视觉重复。
    /// </summary>
    public string SubText => !IsCadInstalled
        ? "本机未安装此版本"
        : Status switch
        {
            PluginDeployStatus.NotInstalled      => "插件尚未部署，可点击安装",
            PluginDeployStatus.DllMissing        => "注册表存在但 DLL 已丢失",
            _                                    => string.Empty
        };

    /// <summary>是否需要显示副文本行。</summary>
    public bool ShowSubText => !string.IsNullOrEmpty(SubText);

    /// <summary>
    /// 已安装时右侧显示的小徽章："最新版" / "旧版"。其他状态返回空。
    /// </summary>
    public string BadgeText => Status switch
    {
        PluginDeployStatus.InstalledCurrent  => "最新版",
        PluginDeployStatus.InstalledOutdated => "旧版",
        _                                    => string.Empty
    };

    /// <summary>是否显示"版本 vX.Y + 徽章"那一行（仅已安装的两种态显示）。</summary>
    public bool ShowVersionRow => Status is PluginDeployStatus.InstalledCurrent
                                          or PluginDeployStatus.InstalledOutdated;

    internal CadEntryViewModel(CadInstallation installation)
    {
        Installation       = installation;
        _status            = installation.Status;
        _installedVersion  = installation.InstalledVersion ?? "—";
    }

    /// <summary>用新的扫描结果刷新本块数据。</summary>
    internal void Refresh(CadInstallation newInstallation)
    {
        Installation     = newInstallation;
        Status           = newInstallation.Status;
        InstalledVersion = newInstallation.InstalledVersion ?? "—";
        OnPropertyChanged(nameof(IsCadInstalled));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(CanUninstall));
    }
}
