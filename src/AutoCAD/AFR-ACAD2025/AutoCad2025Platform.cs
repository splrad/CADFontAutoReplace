using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2025 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称、ldfile 导出符号等版本特定信息。
/// </summary>
internal sealed class AutoCad2025Platform : ICadPlatform, INativeDecodeHookProfileProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2025";
    public string AppName => "AFR-ACAD2025";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2025";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R25.0";  // AutoCAD 2025 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb25.dll";                  // AutoCAD 2025 的数据库 DLL
    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z"; // C++ 修饰名
    public int PrologueSize => 21;                               // ldfile 函数序言指令长度（字节）
    public bool SupportsLdFileHook => true;                      // 2025 支持 ldfile Hook

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFullProfile(
            DisplayName,
            AcDbDllName,
            readStringAcStringRva: 0x8C738,
            readStringWideCharPointerRva: 0x8C830,
            getFilerCodePageIdRva: 0x43B100,
            codePageIdIsDoubleByteRva: 0xBC02EC,
            dbTextDwgInFieldsRva: 0x49910,
            wideStringAssignRva: 0x4EF44,
            multiByteCifToWideCharRva: 0x12965C,
            dTextFullInputProbeRva: 0x6D1660,
            readDoubleByteAnsiRva: 0x6D32A4,
            multiByteToUnicodeAcStringRva: 0x6ACF18,
            codePageFamilyRva: 0x6CFE6C,
            enableDispatcherPatterns: true,
            enableAcPalUtf16Probe: true);
}
