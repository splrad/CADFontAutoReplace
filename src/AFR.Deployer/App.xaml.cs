using AFR.Deployer.Infrastructure;
using AFR.Deployer.ViewModels;
using AFR.Deployer.Views;
using Microsoft.UI.Xaml;

namespace AFR.Deployer;

/// <summary>
/// WinUI 3 应用入口；在 <see cref="OnLaunched"/> 中组装 ViewModel 并显示主窗口。
/// 全局持有 <see cref="MainWindow"/> 引用，避免被 GC 回收导致窗口闪退。
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();

        var dialog       = new WinUiDialogService(_window);
        var folderPicker = new WinUiFolderPickerService(_window);
        var viewModel    = new MainViewModel(dialog, folderPicker);

        _window.Initialize(viewModel);
        _window.Activate();
    }
}
