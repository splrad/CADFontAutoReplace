using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2023 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称、ldfile 导出符号等版本特定信息。
/// </summary>
internal sealed class AutoCad2023Platform : ICadPlatform, INativeDecodeHookProfileProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2023";
    public string AppName => "AFR-ACAD2023";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2023";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R24.2";  // AutoCAD 2023 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb24.dll";                  // AutoCAD 2023 的数据库 DLL
    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z"; // C++ 修饰名
    public int PrologueSize => 21;                               // ldfile 函数序言指令长度（字节）
    public bool SupportsLdFileHook => true;                      // 2023 支持 ldfile Hook

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFullProfile(
            DisplayName,
            AcDbDllName,
            readStringAcStringRva: 0x662EC,
            readStringWideCharPointerRva: 0x66700,
            getFilerCodePageIdRva: 0x3A95C0,
            codePageIdIsDoubleByteRva: 0xAD9644,
            dbTextDwgInFieldsRva: 0x32B80,
            wideStringAssignRva: null,
            multiByteCifToWideCharRva: 0xED7AC,
            dTextFullInputProbeRva: 0x613464,
            readDoubleByteAnsiRva: 0x6151F0,
            multiByteToUnicodeAcStringRva: 0x5EFE38,
            codePageFamilyRva: 0x611CB0,
            enableDispatcherPatterns: false,
            enableAcPalUtf16Probe: false,
            readStringAcStringPrefix: [0x48, 0x89, 0x5C, 0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x30],
            readStringWideCharPointerPrefix: [0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0xE0, 0x00, 0x00, 0x00],
            dbTextDwgInFieldsPrefix: [0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x40, 0x01, 0x00, 0x00],
            readDoubleByteAnsiPrefix: [0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x30]);
}
