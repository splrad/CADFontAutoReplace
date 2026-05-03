using AFR.Abstractions;

namespace AFR;

/// <summary>
/// AutoCAD 2021 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称、ldfile 导出符号等版本特定信息。
/// </summary>
internal sealed class AutoCad2021Platform : ICadPlatform
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2021";
    public string AppName => "AFR-ACAD2021";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2021";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R24.0";  // AutoCAD 2021 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb24.dll";                  // AutoCAD 2021 的数据库 DLL
    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z"; // C++ 修饰名
    public int PrologueSize => 21;                               // ldfile 函数序言指令长度（字节）
    public bool SupportsLdFileHook => true;                      // 2021 支持 ldfile Hook
}
