using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR.Services;

/// <summary>
/// AutoCAD 平台的 <see cref="IFontScanner"/> 实现。
/// <para>
/// UI、样式表检测和 Hook 共用同一份字体可用性索引。
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

    /// <summary>返回共享索引中的 TrueType 字族名和本地化名称。</summary>
    public IReadOnlyCollection<string> ScanSystemTrueTypeFonts()
        => TrueTypeFontAvailabilityIndex.GetAvailableFontNamesSnapshot();
}
