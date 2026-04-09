using System.Runtime.InteropServices;
using System.Windows;
using AFR.Platform;

namespace AFR.UI;

/// <summary>
/// 窗口定位辅助工具。
/// 提供将 WPF 窗口居中到 CAD 宿主窗口所在屏幕的共享方法。
/// </summary>
internal static class WindowPositionHelper
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// 为窗口注册 Loaded 事件，在窗口加载完成后自动居中到 CAD 主窗口所在屏幕。
    /// <para>
    /// 使用 <see cref="PlatformManager.Host"/> 获取宿主窗口句柄，
    /// 并根据 DPI 缩放因子将屏幕像素坐标换算为 WPF 逻辑坐标。
    /// </para>
    /// </summary>
    /// <param name="window">需要居中定位的 WPF 窗口。</param>
    public static void SetupCenterOnParent(Window window)
    {
        window.Loaded += (_, _) => CenterOnParentWindow(window);
    }

    private static void CenterOnParentWindow(Window window)
    {
        IntPtr handle = PlatformManager.Host.MainWindowHandle;
        if (handle == IntPtr.Zero) return;
        if (!GetWindowRect(handle, out var rect)) return;

        // GetWindowRect 返回屏幕像素，WPF Left/Top 使用逻辑单位（96 DPI 基准）
        // 需要按 DPI 缩放因子换算
        var source = PresentationSource.FromVisual(window);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double ownerLeft = rect.Left / scaleX;
        double ownerTop = rect.Top / scaleY;
        double ownerW = (rect.Right - rect.Left) / scaleX;
        double ownerH = (rect.Bottom - rect.Top) / scaleY;

        window.Left = ownerLeft + (ownerW - window.ActualWidth) / 2;
        window.Top = ownerTop + (ownerH - window.ActualHeight) / 2;
    }
}
