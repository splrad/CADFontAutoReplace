using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AFR.Deployer.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AFR.Deployer.ViewModels;

/// <summary>
/// “关于”窗口的静态展示信息与外部链接命令。
/// </summary>
internal sealed partial class AboutViewModel : ObservableObject
{
    private readonly Func<string, string, Task> _showInfoAsync;

    internal AboutViewModel(Func<string, string, Task> showInfoAsync)
    {
        _showInfoAsync = showInfoAsync;
    }

    public static string ProductName => "AFR-CAD 缺失字体自动替换插件部署工具";

    public static string Description => "自动扫描 AutoCAD 2018-2027 并完成 AFR 插件安装、卸载、字体释放和状态检查。";

    public static string VersionText => $"v{DeployerVersionService.GetDisplayVersion()}";

    public static string BuildIdText
    {
        get
        {
            var buildId = DeployerVersionService.GetBuildId();
            return string.IsNullOrWhiteSpace(buildId) ? "未设置" : buildId;
        }
    }

    public static string Author => "splrad 秋夕寻星";

    public static string LicenseName => "Apache License 2.0";

    public static string GitHubUrl => "https://github.com/axiomoth/CADFontAutoReplace";

    public static string GiteeUrl => "https://gitee.com/splrad/CADFontAutoReplace";

    [RelayCommand]
    private Task OpenGitHubAsync() => OpenUrlAsync(GitHubUrl);

    [RelayCommand]
    private Task OpenGiteeAsync() => OpenUrlAsync(GiteeUrl);

    private async Task OpenUrlAsync(string url)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (process is not null) return;
        }
        catch
        {
            // 由下方统一提示手动访问地址。
        }

        await _showInfoAsync($"无法打开浏览器，请手动访问：\n{url}", "AFR 部署工具");
    }
}
