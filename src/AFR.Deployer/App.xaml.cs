using System.Windows;
using AFR.Deployer.Infrastructure;
using AFR.Deployer.ViewModels;
using AFR.Deployer.Views;

namespace AFR.Deployer;

/// <summary>
/// WPF 应用入口；在 <see cref="OnStartup"/> 中组装 ViewModel 并显示主窗口。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window       = new MainWindow();
        var dialog       = new WpfDialogService(window);
        var folderPicker = new WpfFolderPickerService();
        var viewModel    = new MainViewModel(dialog, folderPicker);

        window.Initialize(viewModel);
        window.Show();
    }
}
