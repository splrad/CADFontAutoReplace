using System.Windows;
using System.Windows.Input;

namespace AFR.UI;

/// <summary>
/// 字体选择窗口。
/// 所有数据与 UI 状态由 ViewModel 管理，CodeBehind 仅负责窗口生命周期。
/// </summary>
public partial class FontSelectionWindow : Window
{
    internal FontSelectionViewModel ViewModel { get; }

    /// <summary>用户选择的主字体（已去除首尾空白）。</summary>
    public string SelectedMainFont => ViewModel.SelectedMainFont?.Trim() ?? string.Empty;

    /// <summary>用户选择的大字体（已去除首尾空白）。</summary>
    public string SelectedBigFont => ViewModel.SelectedBigFont?.Trim() ?? string.Empty;

    /// <summary>用户选择的 TrueType 替换字体（已去除首尾空白）。</summary>
    public string SelectedTrueTypeFont => ViewModel.SelectedTrueTypeFont?.Trim() ?? string.Empty;

    public FontSelectionWindow()
    {
        ViewModel = new FontSelectionViewModel();
        DataContext = ViewModel;
        InitializeComponent();
        WindowPositionHelper.SetupCenterOnParent(this);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox combo)
        {
            combo.ApplyTemplate();
            var textBox = combo.Template.FindName("PART_EditableTextBox", combo) as System.Windows.Controls.TextBox;
            if (textBox != null)
                textBox.SelectionBrush = System.Windows.Media.Brushes.Transparent;
        }
    }

    /// <summary>
    /// 支持拖拽无边框窗口。
    /// </summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            this.DragMove();
        }
    }
}
