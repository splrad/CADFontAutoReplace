using AFR.Deployer.ViewModels;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace AFR.Deployer.Views;

/// <summary>
/// WPF-UI FluentWindow 主窗口。
/// </summary>
public partial class MainWindow : FluentWindow
{
    // ── DWM 圆角强制开启：Win11 在 ResizeMode=CanMinimize（无 WS_THICKFRAME）时
    //    默认会绘制直角；通过 DwmSetWindowAttribute 显式请求 ROUND 可恢复圆角。
    //    Win10 等不支持的系统上 API 返回非零，被静默忽略。
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>视图模型，由 <see cref="App"/> 在 <see cref="Initialize"/> 中注入。</summary>
    internal MainViewModel ViewModel { get; private set; } = null!;

    public MainWindow()
    {
        InitializeComponent();
        // ── 多个时机重复设置：FluentWindow 在 SourceInitialized 之后还会调整 WindowChrome/
        //    背景，可能导致首次设置被覆盖；Loaded 与 Activated 兜底，确保最终生效。
        SourceInitialized += (_, _) => ApplyRoundCorners();
        Loaded            += (_, _) => ApplyRoundCorners();
        Activated         += (_, _) => ApplyRoundCorners();
    }

    private void ApplyRoundCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>由 <see cref="App.OnStartup"/> 在创建服务后注入 ViewModel。</summary>
    internal void Initialize(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
    }
}

