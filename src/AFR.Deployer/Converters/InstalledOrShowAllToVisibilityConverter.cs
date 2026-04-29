using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AFR.Deployer.Converters;

/// <summary>
/// 卡片可见性：当条目已安装、或全局 ShowUnavailable=true 时显示。
/// values[0]=IsCadInstalled, values[1]=ShowUnavailable。
/// </summary>
internal sealed class InstalledOrShowAllToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool installed     = values.Length > 0 && values[0] is bool b1 && b1;
        bool showUnavailable = values.Length > 1 && values[1] is bool b2 && b2;
        return (installed || showUnavailable) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
