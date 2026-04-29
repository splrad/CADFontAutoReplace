using AFR.Deployer.ViewModels;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace AFR.Deployer.Views;

/// <summary>
/// WPF-UI FluentWindow 主窗口。
/// </summary>
public partial class MainWindow : FluentWindow
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

    /// <summary>顶部"全选"复选框点击：根据勾选状态对所有可用条目进行选中/取消。</summary>
    private void OnSelectAllClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || ViewModel is null) return;
        var param = cb.IsChecked == true ? "true" : "false";
        if (ViewModel.SelectAllCommand.CanExecute(param))
            ViewModel.SelectAllCommand.Execute(param);
    }
}

