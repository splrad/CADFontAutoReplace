namespace AFR.Abstractions;

/// <summary>
/// CAD 宿主环境的通用操作抽象。
/// <para>
/// 用于解耦 AFR.UI 层对特定 CAD API 的直接依赖。
/// 例如在 AutoCAD 中，显示模态窗口需要调用专用 API 而非直接 ShowDialog，
/// 因此通过此接口让 UI 层不必关心具体平台的窗口管理方式。
/// </para>
/// </summary>
public interface ICadHost
{
    /// <summary>
    /// CAD 主窗口的原生窗口句柄 (HWND)。
    /// 用于将 WPF 弹出窗口定位到 CAD 所在屏幕的中心位置。
    /// </summary>
    nint MainWindowHandle { get; }

    /// <summary>
    /// 以模态方式显示一个 WPF 窗口。
    /// CAD 平台负责将窗口正确挂载到宿主应用程序的窗口层级中。
    /// </summary>
    /// <param name="window">要显示的 WPF 窗口实例。</param>
    void ShowModalWindow(object window);
}
