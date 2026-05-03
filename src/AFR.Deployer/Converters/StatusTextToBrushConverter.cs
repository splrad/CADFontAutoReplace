using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AFR.Deployer.Converters;

/// <summary>
/// 状态文本 → 颜色画刷：按 StatusText 语义前缀区分。
/// <list type="bullet">
///   <item><description>⚠ 警告（CAD 运行）：橙</description></item>
///   <item><description>✓ 成功：绿</description></item>
///   <item><description>"正在…"：蓝</description></item>
///   <item><description>其他中性态（就绪/扫描中/未检测到）：灰</description></item>
/// </list>
/// ConverterParameter="bg" 返回淡色背景，否则返回前景色。
/// </summary>
internal sealed class StatusTextToBrushConverter : IValueConverter
{
    private static readonly Color WarnFg = Color.FromArgb(0xFF, 0xB7, 0x60, 0x00);
    private static readonly Color WarnBg = Color.FromArgb(0xFF, 0xFC, 0xEF, 0xD7);
    private static readonly Color OkFg   = Color.FromArgb(0xFF, 0x10, 0x88, 0x4B);
    private static readonly Color OkBg   = Color.FromArgb(0xFF, 0xE6, 0xF4, 0xEC);
    private static readonly Color BusyFg = Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD);
    private static readonly Color BusyBg = Color.FromArgb(0xFF, 0xE3, 0xEF, 0xFA);
    private static readonly Color IdleFg = Color.FromArgb(0xFF, 0x5C, 0x5C, 0x5C);
    private static readonly Color IdleBg = Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        var isBg = parameter is string s && s == "bg";

        Color color;
        if (text.StartsWith('⚠'))
            color = isBg ? WarnBg : WarnFg;
        else if (text.StartsWith('✓'))
            color = isBg ? OkBg : OkFg;
        else if (text.Contains("正在"))
            color = isBg ? BusyBg : BusyFg;
        else
            color = isBg ? IdleBg : IdleFg;

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
