using System.Windows;
using AFR.Deployer.ViewModels;

namespace AFR.Deployer.Views;

/// <summary>
/// WPF 主窗口；尺寸固定为 940x600 逻辑像素，由 WPF 自动按 DPI 缩放。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>视图模型，由 <see cref="App"/> 在 <see cref="Initialize"/> 中注入。</summary>
    internal MainViewModel ViewModel { get; private set; } = null!;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>由 <see cref="App.OnStartup"/> 在创建服务后注入 ViewModel。</summary>
    internal void Initialize(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
    }
}
