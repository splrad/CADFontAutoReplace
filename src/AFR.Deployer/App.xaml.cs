using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AFR.Deployer.Infrastructure;
using AFR.Deployer.ViewModels;
using AFR.Deployer.Views;
using Wpf.Ui.Appearance;

namespace AFR.Deployer;

/// <summary>
/// WPF 应用入口；在 <see cref="OnStartup"/> 中组装 ViewModel 并显示主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 全局字体单一来源。XAML 通过 <c>{x:Static local:App.EmbeddedFontFamily}</c> 引用，
    /// 代码通过 <see cref="EmbeddedFontFamily"/> 引用，<see cref="TextElement.FontFamilyProperty"/>
    /// 默认值也指向它。整个进程内只此一份 <see cref="FontFamily"/> 实例。
    /// </summary>
    public static FontFamily EmbeddedFontFamily { get; }

    /// <summary>
    /// 静态构造：在任何 XAML 解析与控件实例化之前覆盖 FontFamily 依赖属性的默认值，
    /// 使所有未显式设置 FontFamily 的元素（含 WPF-UI FluentWindow / 按钮 / 文本块等）
    /// 一律使用嵌入的鸿蒙黑体。Control / TextBlock 的 FontFamilyProperty 是从
    /// TextElement.FontFamilyProperty AddOwner 而来，覆盖根属性即可全局生效。
    /// </summary>
    static App()
    {
        // 触发 pack URI scheme 注册：App 静态构造阶段 Application 基类 type initializer
        // 不一定已运行，直接 new Uri("pack://...") 会抛 "Invalid port specified"。
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

        // 仅指定嵌入字体的 Family Name；不附加 Segoe UI / YaHei 等系统字体回退，
        // 这样如果 ttf 加载失败，UI 会回退到 WPF 默认 (Tahoma / Times New Roman)，
        // 视觉上明显不同——可以一眼判断"嵌入字体到底有没有生效"。
        EmbeddedFontFamily = new FontFamily(
            new Uri("pack://application:,,,/AFR.Deployer;component/", UriKind.Absolute),
            "./Fonts/#HarmonyOS Sans SC");

        // 单点覆盖：TextElement.FontFamilyProperty 是 Control / TextBlock / FlowDocument
        // 等所有可显示文本元素共享的依赖属性。覆盖它的默认值后，整个 App 内未显式设置
        // FontFamily 的元素都会自动使用嵌入字体——这就是"所有控件只从一个地方拿字体"。
        TextElement.FontFamilyProperty.OverrideMetadata(
            typeof(TextElement),
            new FrameworkPropertyMetadata(EmbeddedFontFamily));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window       = new MainWindow();
        var dialog       = new WpfDialogService(window);
        var folderPicker = new WpfFolderPickerService();
        var viewModel    = new MainViewModel(dialog, folderPicker);

        window.Initialize(viewModel);

        // 启用 WPF-UI 主题与 Mica 背景；窗口必须先创建后再 Apply。
        ApplicationThemeManager.Apply(window);

        window.Show();
    }
}
