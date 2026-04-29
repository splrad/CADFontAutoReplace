using System.Runtime.InteropServices;
using AFR.Deployer.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace AFR.Deployer.Views;

/// <summary>
/// 主窗口：自定义 TitleBar、Mica 背景、固定大小且 DPI 感知。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>逻辑像素下的目标窗口尺寸（96 DPI 基准）。</summary>
    private const int LogicalWidth  = 940;
    private const int LogicalHeight = 600;

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

        ConfigureFixedSize();
    }

    /// <summary>
    /// 按当前显示器 DPI 缩放逻辑尺寸，并禁用最大化与缩放，确保窗口为固定大小但在所有 DPI 下视觉一致。
    /// </summary>
    private void ConfigureFixedSize()
    {
        var hwnd      = WindowNative.GetWindowHandle(this);
        var windowId  = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // DPI 缩放：Win32 GetDpiForWindow 返回每英寸像素数，96 为 100%
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        int physicalWidth  = (int)(LogicalWidth  * scale);
        int physicalHeight = (int)(LogicalHeight * scale);

        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable    = false;
            presenter.IsMaximizable  = false;
            presenter.IsMinimizable  = true;
        }
    }

    /// <summary>由 <see cref="App.OnLaunched"/> 在创建服务后注入 ViewModel。</summary>
    internal void Initialize(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        RootGrid.DataContext = viewModel;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(System.IntPtr hwnd);
}
