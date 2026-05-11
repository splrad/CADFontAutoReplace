#if DEBUG

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace AFR.UI;

internal sealed class MTextInsertTemplateItem
{
    public string Name { get; }

    public string Contents { get; }

    public MTextInsertTemplateItem(string name, string contents)
    {
        Name = name;
        Contents = contents;
    }
}

internal sealed class MTextInsertViewModel : INotifyPropertyChanged
{
    private bool _isTemplateMode = true;
    private MTextInsertTemplateItem? _selectedTemplate;
    private string _customInput = string.Empty;

    public ObservableCollection<MTextInsertTemplateItem> Templates { get; }

    public ICommand PasteFromClipboardCommand { get; }

    public ICommand InsertCommand { get; }

    public ICommand CancelCommand { get; }

    public string? ResultContents { get; private set; }

    public bool IsTemplateMode
    {
        get => _isTemplateMode;
        set
        {
            if (_isTemplateMode == value) return;

            _isTemplateMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomMode));
        }
    }

    public bool IsCustomMode
    {
        get => !IsTemplateMode;
        set => IsTemplateMode = !value;
    }

    public MTextInsertTemplateItem? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (ReferenceEquals(_selectedTemplate, value)) return;

            _selectedTemplate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TemplatePreview));
        }
    }

    public string TemplatePreview => SelectedTemplate?.Contents ?? string.Empty;

    public string CustomInput
    {
        get => _customInput;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_customInput, next, StringComparison.Ordinal)) return;

            _customInput = next;
            OnPropertyChanged();
        }
    }

    public event EventHandler<UiDialogCloseRequestedEventArgs>? CloseRequested;

    public event EventHandler<MTextInsertMessageRequestedEventArgs>? MessageRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MTextInsertViewModel()
    {
        Templates = new ObservableCollection<MTextInsertTemplateItem>
        {
            new("综合测试 — 全部边界情况", BuildComprehensiveTemplate()),
            new("SHX 缺失字体组合", BuildShxTemplate()),
            new("TrueType 格式变体", BuildTrueTypeTemplate()),
            new("路径残留（旧版 CAD 安装目录）", BuildPathTemplate()),
            new("混合字体切换", BuildMixedTemplate()),
        };

        SelectedTemplate = Templates.Count > 0 ? Templates[0] : null;
        PasteFromClipboardCommand = new UiRelayCommand(PasteFromClipboard);
        InsertCommand = new UiRelayCommand(Insert);
        CancelCommand = new UiRelayCommand(() => CloseRequested?.Invoke(this, new UiDialogCloseRequestedEventArgs(null)));
    }

    private void PasteFromClipboard()
    {
        if (Clipboard.ContainsText())
            CustomInput = Clipboard.GetText();
    }

    private void Insert()
    {
        ResultContents = IsTemplateMode
            ? SelectedTemplate?.Contents
            : CustomInput.Trim();

        if (string.IsNullOrEmpty(ResultContents))
        {
            MessageRequested?.Invoke(this, new MTextInsertMessageRequestedEventArgs(
                "内容为空，无法插入。",
                "提示",
                MessageBoxImage.Warning));
            return;
        }

        CloseRequested?.Invoke(this, new UiDialogCloseRequestedEventArgs(true));
    }

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class MTextInsertMessageRequestedEventArgs : EventArgs
{
    public string Message { get; }

    public string Title { get; }

    public MessageBoxImage Image { get; }

    public MTextInsertMessageRequestedEventArgs(string message, string title, MessageBoxImage image)
    {
        Message = message;
        Title = title;
        Image = image;
    }
}

#endif
