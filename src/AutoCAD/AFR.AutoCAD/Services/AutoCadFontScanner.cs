using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR.Services;

/// <summary>
/// AutoCAD 平台的 <see cref="IFontScanner"/> 实现。
/// <para>
/// SHX 和 TrueType 均委托共享字体索引，保证样式表、Hook 和 UI 使用同一份可用性判断。
/// UI 主字体/大字体列表仍分别取常规 SHX 和大字体 SHX，不合并展示。
/// </para>
/// </summary>
internal sealed class AutoCadFontScanner : IFontScanner
{
    /// <summary>返回共享索引中所有可用 SHX 字体文件名。</summary>
    public IReadOnlyCollection<string> ScanAvailableShxFonts()
        => ShxFontAvailabilityIndex.GetAllFontNamesSnapshot();

    /// <summary>返回共享索引中可识别为常规 SHX 主字体的文件名。</summary>
    public IReadOnlyCollection<string> ScanAvailableMainShxFonts()
        => ShxFontAvailabilityIndex.GetMainFontNamesSnapshot();

    /// <summary>返回共享索引中可识别为 SHX 大字体的文件名。</summary>
    public IReadOnlyCollection<string> ScanAvailableBigShxFonts()
        => ShxFontAvailabilityIndex.GetBigFontNamesSnapshot();

    /// <summary>
    /// 扫描系统已安装的 TrueType 字体，返回 DirectWrite 枚举到的字体族名和本地化名称。
    /// </summary>
    public IReadOnlyCollection<string> ScanSystemTrueTypeFonts()
        => TrueTypeFontAvailabilityIndex.GetAvailableFontNamesSnapshot();
}
