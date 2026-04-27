using System.Windows;
using AFR.Deployer.Infrastructure;
using AFR.Deployer.ViewModels;
using AFR.Deployer.Views;

namespace AFR.Deployer;

/// <summary>
/// WPF 应用程序入口点。手动组装服务并创建主窗口，不使用 StartupUri。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dialog       = new WpfDialogService();
        var folderPicker = new WpfFolderPickerService();
        var viewModel    = new MainViewModel(dialog, folderPicker);
        var window       = new MainWindow(viewModel);
        window.Show();
    }
}
