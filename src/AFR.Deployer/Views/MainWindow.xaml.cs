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
        SourceInitialized += OnSourceInitializedApplyRoundCorners;
    }

    private static void OnSourceInitializedApplyRoundCorners(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        var hwnd = new WindowInteropHelper(window).Handle;
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

    /// <summary>顶部"全选"复选框点击：根据勾选状态对所有可用条目进行选中/取消。</summary>
    private void OnSelectAllClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || ViewModel is null) return;
        var param = cb.IsChecked == true ? "true" : "false";
        if (ViewModel.SelectAllCommand.CanExecute(param))
            ViewModel.SelectAllCommand.Execute(param);
    }
}

