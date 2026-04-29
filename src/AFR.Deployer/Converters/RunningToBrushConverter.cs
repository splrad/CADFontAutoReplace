using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AFR.Deployer.Converters;

/// <summary>
/// CAD 是否运行映射到状态点颜色：运行=橙色警告，否则=绿色就绪。
/// </summary>
internal sealed class RunningToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var running = value is bool b && b;
        return running
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x83, 0x00))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x8B, 0x44));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
