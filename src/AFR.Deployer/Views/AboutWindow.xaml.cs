using AFR.Deployer.ViewModels;
using Wpf.Ui.Controls;

namespace AFR.Deployer.Views;

/// <summary>
/// 部署器“关于”二级窗口。
/// </summary>
public partial class AboutWindow : FluentWindow
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    internal void Initialize(AboutViewModel viewModel)
    {
        DataContext = viewModel;
    }
}
