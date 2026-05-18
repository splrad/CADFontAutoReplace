using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2026 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称、ldfile 导出符号等版本特定信息。
/// </summary>
internal sealed class AutoCad2026Platform : ICadPlatform, INativeDecodeHookProfileProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2026";
    public string AppName => "AFR-ACAD2026";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2026";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R25.1";  // AutoCAD 2026 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb25.dll";                  // AutoCAD 2026 的数据库 DLL
    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z"; // C++ 修饰名
    public int PrologueSize => 21;                               // ldfile 函数序言指令长度（字节）
    public bool SupportsLdFileHook => true;                      // 2026 支持 ldfile Hook

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFullProfile(
            DisplayName,
            AcDbDllName,
            readStringAcStringRva: 0x93E70,
            readStringWideCharPointerRva: 0x93F60,
            getFilerCodePageIdRva: 0x4478E0,
            codePageIdIsDoubleByteRva: 0xBCF310,
            dbTextDwgInFieldsRva: 0x33690,
            wideStringAssignRva: 0x5EFEC,
            multiByteCifToWideCharRva: 0xD71A8,
            dTextFullInputProbeRva: 0x6DE5B0,
            readDoubleByteAnsiRva: 0x6E01F8,
            multiByteToUnicodeAcStringRva: 0x6B9F04,
            codePageFamilyRva: 0x6DCDC0,
            enableDispatcherPatterns: true,
            enableAcPalUtf16Probe: true,
            acPalUtf16ToWideGetWideBufferRva: 0x54D50,
            mainDispatcherRva: 0x6DEB90,
            parallelDispatcherRva: 0x6DE674);
}
