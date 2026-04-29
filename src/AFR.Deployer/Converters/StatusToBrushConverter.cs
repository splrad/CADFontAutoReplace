using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AFR.Deployer.Models;

namespace AFR.Deployer.Converters;

/// <summary>
/// 把 <see cref="PluginDeployStatus"/> 映射为状态点颜色 Brush。
/// </summary>
internal sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PluginDeployStatus s)
        {
            return s switch
            {
                PluginDeployStatus.InstalledCurrent  => new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x8B, 0x44)),
                PluginDeployStatus.InstalledOutdated => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x83, 0x00)),
                PluginDeployStatus.DllMissing        => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
                _                                    => new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88)),
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
