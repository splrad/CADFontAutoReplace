using AFR.Abstractions;

namespace AFR.Hosting;

/// <summary>
/// AutoCAD 平台的 <see cref="ICadHost"/> 实现。
/// 将通用的窗口操作适配为 AutoCAD 特定的 API 调用。
/// </summary>
internal sealed class AutoCadHost : ICadHost
{
    /// <inheritdoc/>
    public nint MainWindowHandle =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.MainWindow.Handle;

    /// <summary>
    /// 通过 AutoCAD 专用 API 显示模态 WPF 窗口。
    /// 必须使用此方法而非 Window.ShowDialog()，否则窗口不会正确绑定到 AutoCAD 主窗口。
    /// </summary>
    /// <param name="window">要显示的 WPF 窗口实例，必须是 <see cref="System.Windows.Window"/> 类型。</param>
    public void ShowModalWindow(object window)
    {
        if (window is System.Windows.Window wpfWindow)
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(wpfWindow);
    }
}
