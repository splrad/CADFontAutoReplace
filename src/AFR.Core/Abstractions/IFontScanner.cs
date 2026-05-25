namespace AFR.Abstractions;

/// <summary>
/// 字体扫描抽象接口，用于获取当前 CAD 环境中可用的字体列表。
/// <para>
/// 各 CAD 平台根据自身的搜索路径和字体目录提供具体实现。
/// 扫描结果用于判断某个字体是否缺失（即不在可用列表中）。
/// </para>
/// </summary>
public interface IFontScanner
{
    /// <summary>扫描 CAD 搜索路径下的可用 SHX 字体文件名（不含路径，含扩展名）。</summary>
    IReadOnlyCollection<string> ScanAvailableShxFonts();

    /// <summary>扫描 CAD 搜索路径下可用的常规 SHX 主字体文件名（不含路径，含扩展名）。</summary>
    IReadOnlyCollection<string> ScanAvailableMainShxFonts();

    /// <summary>扫描 CAD 搜索路径下可用的 SHX 大字体文件名（不含路径，含扩展名）。</summary>
    IReadOnlyCollection<string> ScanAvailableBigShxFonts();

    /// <summary>扫描操作系统已安装的 TrueType 字体族名（Family Name）。</summary>
    IReadOnlyCollection<string> ScanSystemTrueTypeFonts();
}
