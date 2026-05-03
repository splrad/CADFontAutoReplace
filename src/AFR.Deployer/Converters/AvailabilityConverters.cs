using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AFR.Deployer.Converters;

/// <summary>
/// 把可用性映射为卡片背景：true 用普通透明微填充；false 用更深的不可用色（带轻微红灰色调）以明确禁用状态。
/// </summary>
internal sealed class AvailabilityToCardBackgroundConverter : IValueConverter
{
    private static readonly Brush Available   = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
    private static readonly Brush Unavailable = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Available : Unavailable;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把可用性映射为徽标背景：可用红色，不可用灰色。</summary>
internal sealed class AvailabilityToBadgeBrushConverter : IValueConverter
{
    private static readonly Brush Available   = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0x39, 0x35));
    private static readonly Brush Unavailable = new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Available : Unavailable;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把可用性映射为主文本前景：可用主前景色，不可用次要前景色。</summary>
internal sealed class AvailabilityToTextBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && !b)
        {
            return new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));
        }
        return new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把已安装/未安装映射为状态徽章背景：已安装用次要中性色，未安装用警示橙红色以明确禁用。</summary>
internal sealed class AvailabilityToBadgeBackgroundConverter : IValueConverter
{
    private static readonly Brush Installed    = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
    private static readonly Brush NotInstalled = new SolidColorBrush(Color.FromArgb(0x33, 0xC4, 0x2B, 0x1C));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Installed : NotInstalled;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔到 Visibility（true → Visible，false → Collapsed）。</summary>
internal sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
