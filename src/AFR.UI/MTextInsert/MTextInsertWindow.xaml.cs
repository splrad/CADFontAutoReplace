#if DEBUG

using System.Windows;
using System.Windows.Input;

namespace AFR.UI;

/// <summary>
/// MText 插入器窗口。
/// 提供预设模板选择和自定义格式代码输入两种模式，返回用户确认的 MText 内容。
/// </summary>
public partial class MTextInsertWindow : Window
{
    /// <summary>用户确认的 MText 内容。为 null 表示取消。</summary>
    public string? ResultContents { get; private set; }

    private readonly (string Name, string Contents)[] _templates = new[]
    {
        ("综合测试 — 全部边界情况", BuildComprehensiveTemplate()),
        ("SHX 缺失字体组合", BuildShxTemplate()),
        ("TrueType 格式变体", BuildTrueTypeTemplate()),
        ("路径残留（旧版 CAD 安装目录）", BuildPathTemplate()),
        ("混合字体切换", BuildMixedTemplate()),
    };

    /// <summary>
    /// 初始化 MText 插入器窗口。
    /// </summary>
    public MTextInsertWindow()
    {
        InitializeComponent();
        WindowPositionHelper.SetupCenterOnParent(this);

        // 填充模板列表
        foreach (var (name, _) in _templates)
            CbTemplates.Items.Add(name);
        CbTemplates.SelectedIndex = 0;
    }

    #region 事件处理

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (TemplatePanel == null || CustomPanel == null) return;

        bool isTemplate = RbTemplate.IsChecked == true;
        TemplatePanel.Visibility = isTemplate ? Visibility.Visible : Visibility.Collapsed;
        CustomPanel.Visibility = isTemplate ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTemplateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int idx = CbTemplates.SelectedIndex;
        if (idx >= 0 && idx < _templates.Length)
            TbTemplatePreview.Text = _templates[idx].Contents;
    }

    private void OnPasteFromClipboard(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
            TbCustomInput.Text = Clipboard.GetText();
    }

    private void OnInsert(object sender, RoutedEventArgs e)
    {
        if (RbTemplate.IsChecked == true)
        {
            int idx = CbTemplates.SelectedIndex;
            ResultContents = idx >= 0 && idx < _templates.Length ? _templates[idx].Contents : null;
        }
        else
        {
            string text = TbCustomInput.Text?.Trim() ?? "";
            ResultContents = text.Length > 0 ? text : null;
        }

        if (string.IsNullOrEmpty(ResultContents))
        {
            HandyControl.Controls.MessageBox.Show("内容为空，无法插入。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    #endregion

    #region 预设模板

    /// <summary>综合测试 — 覆盖所有已知边界情况。</summary>
    private static string BuildComprehensiveTemplate()
    {
        return
            @"{\fFakeShxFont|b0|i0|c0|p0;\H1.2x;\C1;CAD字体综合测试开始\P" +
            @"{\FNoExistShx,BigNoExist|b0|i0|c0|p0;\H0.8x;\C3;SHX主字体+大字体组合测试\P}" +
            @"{\fNonExistTTF_A|b1|i0|c0|p0;\H1.0x;\C5;TrueType不存在字体（加粗）测试\P}" +
            @"{\fNonExistTTF_B|b0|i1|c0|p0;\H1.0x;\C6;TrueType不存在字体（斜体）测试\P}" +
            @"{\fSimHei|b0|i0|c134|p2;\C7;已有TrueType黑体中文\P}" +
            @"{\Ftxt,hztxt|\C256;已有SHX字体txt+hztxt\P}" +
            @"{\FC:/OldPath/AutoCAD2024/txt,C:/OldPath/AutoCAD2024/hztxt|路径残留SHX测试\P}" +
            @"{\FNoExistShx|仅主字体缺失\P}" +
            @"{\Fgbenor,@gbcbig|已有字体+@前缀大字体\P}" +
            @"\fSimSun|b0|i0|c134|p2;综合测试结束}";
    }

    /// <summary>SHX 缺失字体组合。</summary>
    private static string BuildShxTemplate()
    {
        return
            @"{\FNoExistShx,BigNoExist|缺失SHX主+大字体测试文本\P}" +
            @"{\FNoExistShx|仅主字体缺失测试文本\P}" +
            @"{\Fgbenor,@gbcbig|已有字体+@前缀大字体\P}" +
            @"{\Ftxt,hztxt|已有SHX字体txt+hztxt}";
    }

    /// <summary>TrueType 格式变体。</summary>
    private static string BuildTrueTypeTemplate()
    {
        return
            @"{\fNonExistTTF|b0|i0|c0|p0;完整参数缺失TrueType\P}" +
            @"{\fNonExistTTF_B;分号结束缺失TrueType\P}" +
            @"{\fSimHei|b0|i0|c134|p2;已有TrueType黑体\P}" +
            @"{\fSimHei|简写格式pipe结束\P}" +
            @"{\fFangSong_GB2312|b0|i0|c134|p49;仿宋GB2312}";
    }

    /// <summary>路径残留（旧版 CAD 安装目录）。</summary>
    private static string BuildPathTemplate()
    {
        return
            @"{\FC:/Software/Autodesk/AutoCAD 2024/Support/txt,C:/Software/Autodesk/AutoCAD 2024/Support/hztxt|路径SHX测试第一行\P}" +
            @"{\FC:/OldPath/fonts/gbenor,@C:/OldPath/fonts/gbcbig|路径+@前缀测试\P}" +
            @"{\Ftxt,hztxt|正常SHX对照组}";
    }

    /// <summary>混合字体切换。</summary>
    private static string BuildMixedTemplate()
    {
        return
            @"{\Ftxt,hztxt|SHX中文测试}" +
            @"{\Fgbenor,@gbcbig|切换到gbenor}" +
            @"{\fSimSun|b0|i0|c134|p2;切换到宋体TrueType}" +
            @"{\FNoExistShx,BigNoExist|切换到缺失SHX}" +
            @"{\fNonExistTTF|b0|i0|c0|p0;切换到缺失TrueType}" +
            @"{\fSimHei|b0|i0|c134|p2;最终切换回黑体}";
    }

    #endregion
}

#endif
