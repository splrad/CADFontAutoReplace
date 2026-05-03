using System;
using System.Globalization;
using System.Windows.Data;

namespace AFR.Deployer.Converters;

/// <summary>
/// 把布尔可用性映射为不透明度：true 完全显示（1.0），false 半透明（0.45）。
/// </summary>
internal sealed class AvailabilityToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? 1.0 : 0.45;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
