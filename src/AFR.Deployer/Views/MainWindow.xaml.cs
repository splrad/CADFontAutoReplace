using AFR.Deployer.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace AFR.Deployer.Views;

/// <summary>
/// 主窗口：自定义 TitleBar、Mica 背景。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>视图模型，由 <see cref="App"/> 在 <see cref="Initialize"/> 中注入。</summary>
    internal MainViewModel ViewModel { get; private set; } = null!;

    public MainWindow()
    {
        InitializeComponent();

        // 扩展到内容区域的自定义标题栏（仅图标 + 应用名）
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Mica（Win11 原生；旧系统自动退化为纯色）
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }

        Title = "AFR 部署工具";

        // 初始窗口大小
        var hwnd      = WindowNative.GetWindowHandle(this);
        var windowId  = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(960, 660));
    }

    /// <summary>由 <see cref="App.OnLaunched"/> 在创建服务后注入 ViewModel。</summary>
    internal void Initialize(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        RootGrid.DataContext = viewModel;
    }
}
