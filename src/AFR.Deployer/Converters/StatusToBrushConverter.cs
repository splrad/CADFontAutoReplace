using AFR.Deployer.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace AFR.Deployer.Converters;

/// <summary>
/// 把 <see cref="PluginDeployStatus"/> 映射为状态点颜色 Brush。
/// </summary>
internal sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
    {
        if (value is PluginDeployStatus s)
        {
            return s switch
            {
                PluginDeployStatus.InstalledCurrent  => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x10, 0x8B, 0x44)),
                PluginDeployStatus.InstalledOutdated => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xC4, 0x83, 0x00)),
                PluginDeployStatus.DllMissing        => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
                _                                    => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x88, 0x88, 0x88)),
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => throw new System.NotSupportedException();
}
