using AFR.Deployer.ViewModels;
using HandyControl.Controls;

namespace AFR.Deployer.Views;

/// <summary>
/// 主窗口。ViewModel 由 <see cref="App"/> 通过构造函数注入。
/// 继承 <see cref="HandyControl.Controls.Window"/> 以获得 HC 无边框窗口样式。
/// </summary>
public partial class MainWindow : Window
{
    internal MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
