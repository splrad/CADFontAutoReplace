using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.MTextEditor;

/// <summary>
/// MText 查看器窗口。
/// 显示带语法高亮的 MText 原始代码，只读。
/// </summary>
public partial class MTextEditorWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    internal MTextEditorWindow(string rawContents)
    {
        InitializeComponent();

        // 在 Loaded 事件中居中，确保在 ShowModalWindow 定位之后执行，
        // 且 PresentationSource 可用于 DPI 换算
        Loaded += (_, _) => CenterOnAcadWindow();

        string displayText = MTextEditorViewModel.ToDisplayFormat(rawContents);
        RawViewer.Document = MTextSyntaxHighlighter.CreateHighlightedRawDocument(displayText);
    }

    private void CenterOnAcadWindow()
    {
        if (!GetWindowRect(AcadApp.MainWindow.Handle, out var rect)) return;

        // GetWindowRect 返回屏幕像素，WPF Left/Top 使用逻辑单位（96 DPI 基准）
        // 需要按 DPI 缩放因子换算
        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double ownerLeft = rect.Left / scaleX;
        double ownerTop = rect.Top / scaleY;
        double ownerW = (rect.Right - rect.Left) / scaleX;
        double ownerH = (rect.Bottom - rect.Top) / scaleY;

        Left = ownerLeft + (ownerW - ActualWidth) / 2;
        Top = ownerTop + (ownerH - ActualHeight) / 2;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
