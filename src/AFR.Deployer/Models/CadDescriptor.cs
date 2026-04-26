namespace AFR.Deployer.Models;

/// <summary>
/// 编译期已知的 CAD 版本静态描述符，与 ICadPlatform 对应但不依赖插件程序集。
/// <para>
/// 所有支持的版本在 <see cref="CadDescriptors.All"/> 中统一维护。
/// </para>
/// </summary>
/// <param name="Brand">CAD 品牌，如 "AutoCAD"。</param>
/// <param name="Version">CAD 版本年份，如 "2025"。</param>
/// <param name="DisplayName">UI 显示名称，如 "AutoCAD 2025"。</param>
/// <param name="RegistryBasePath">注册表基路径，如 <c>Software\Autodesk\AutoCAD\R25.0</c>。</param>
/// <param name="AppName">注册表 Applications 子键名，如 "AFR-ACAD2025"。</param>
/// <param name="EmbeddedResourceKey">嵌入资源的清单名称，用于提取 DLL。</param>
internal sealed record CadDescriptor(
    string Brand,
    string Version,
    string DisplayName,
    string RegistryBasePath,
    string AppName,
    string EmbeddedResourceKey);

/// <summary>
/// 所有受支持 CAD 版本的静态元数据表。
/// <para>
/// 注册表键模式固定为 <c>^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$</c>，对所有 AutoCAD 版本通用，
/// 由 <see cref="Services.CadRegistryScanner"/> 直接使用常量，不在此处重复声明。
/// </para>
/// </summary>
internal static class CadDescriptors
{
    /// <summary>按版本年份升序排列的所有支持版本。</summary>
    internal static readonly IReadOnlyList<CadDescriptor> All =
    [
        new("AutoCAD", "2018", "AutoCAD 2018", @"Software\Autodesk\AutoCAD\R22.0", "AFR-ACAD2018", "AFR.Deployer.Resources.AFR-ACAD2018.dll"),
        new("AutoCAD", "2019", "AutoCAD 2019", @"Software\Autodesk\AutoCAD\R23.0", "AFR-ACAD2019", "AFR.Deployer.Resources.AFR-ACAD2019.dll"),
        new("AutoCAD", "2020", "AutoCAD 2020", @"Software\Autodesk\AutoCAD\R23.1", "AFR-ACAD2020", "AFR.Deployer.Resources.AFR-ACAD2020.dll"),
        new("AutoCAD", "2021", "AutoCAD 2021", @"Software\Autodesk\AutoCAD\R24.0", "AFR-ACAD2021", "AFR.Deployer.Resources.AFR-ACAD2021.dll"),
        new("AutoCAD", "2022", "AutoCAD 2022", @"Software\Autodesk\AutoCAD\R24.1", "AFR-ACAD2022", "AFR.Deployer.Resources.AFR-ACAD2022.dll"),
        new("AutoCAD", "2023", "AutoCAD 2023", @"Software\Autodesk\AutoCAD\R24.2", "AFR-ACAD2023", "AFR.Deployer.Resources.AFR-ACAD2023.dll"),
        new("AutoCAD", "2024", "AutoCAD 2024", @"Software\Autodesk\AutoCAD\R24.3", "AFR-ACAD2024", "AFR.Deployer.Resources.AFR-ACAD2024.dll"),
        new("AutoCAD", "2025", "AutoCAD 2025", @"Software\Autodesk\AutoCAD\R25.0", "AFR-ACAD2025", "AFR.Deployer.Resources.AFR-ACAD2025.dll"),
        new("AutoCAD", "2026", "AutoCAD 2026", @"Software\Autodesk\AutoCAD\R25.1", "AFR-ACAD2026", "AFR.Deployer.Resources.AFR-ACAD2026.dll"),
        new("AutoCAD", "2027", "AutoCAD 2027", @"Software\Autodesk\AutoCAD\R26.0", "AFR-ACAD2027", "AFR.Deployer.Resources.AFR-ACAD2027.dll"),
    ];
}
