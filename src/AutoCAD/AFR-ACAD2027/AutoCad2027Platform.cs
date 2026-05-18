using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2027 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称、ldfile 导出符号等版本特定信息。
/// </summary>
internal sealed class AutoCad2027Platform : ICadPlatform, INativeDecodeHookProfileProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2027";
    public string AppName => "AFR-ACAD2027";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2027";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R26.0";  // AutoCAD 2027 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb26.dll";                  // AutoCAD 2027 的数据库 DLL
    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z"; // C++ 修饰名
    public int PrologueSize => 21;                               // ldfile 函数序言指令长度（字节）
    public bool SupportsLdFileHook => true;                      // 2027 支持 ldfile Hook

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFullProfile(
            DisplayName,
            AcDbDllName,
            readStringAcStringRva: 0x4EF70,
            readStringWideCharPointerRva: null,
            getFilerCodePageIdRva: 0x4579A4,
            codePageIdIsDoubleByteRva: 0xBF2344,
            dbTextDwgInFieldsRva: 0x2E740,
            wideStringAssignRva: null,
            multiByteCifToWideCharRva: 0x140220,
            dTextFullInputProbeRva: 0x6E90DC,
            readDoubleByteAnsiRva: 0x6EAD14,
            multiByteToUnicodeAcStringRva: 0x6C4A0C,
            codePageFamilyRva: 0x6E79A0,
            enableDispatcherPatterns: false,
            enableAcPalUtf16Probe: false,
            readStringAcStringPrefix: [0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x10, 0x01, 0x00, 0x00],
            dbTextDwgInFieldsPrefix: [0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x30, 0x02, 0x00, 0x00],
            multiByteCifToWideCharPrefix: [0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55]);
}
