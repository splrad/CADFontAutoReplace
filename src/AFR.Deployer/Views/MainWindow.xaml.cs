using System.Windows;

namespace AFR.Deployer.Views;

/// <summary>
/// 主窗口。DataContext 在 XAML 中通过 <c>&lt;vm:MainViewModel /&gt;</c> 直接声明。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
