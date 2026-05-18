using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2021 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称、ldfile 导出符号等版本特定信息。
/// </summary>
internal sealed class AutoCad2021Platform : ICadPlatform, INativeDecodeHookProfileProvider
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

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFailClosedProfile(
            DisplayName,
            AcDbDllName,
            "2021 acdb 基线缺少 acdbGetFilerCodePageId 导出且虚表 resolver 未验证，DBText AI native hook fail closed。");
}
