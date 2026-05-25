using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2018 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2018Platform : ICadPlatform, INativeFontHookExportsProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2018";
    public string AppName => "AFR-ACAD2018";
    public string DisplayName => "AutoCAD 2018";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R22.0";
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$";
    public string AcDbDllName => "acdb22.dll";
    public bool SupportsNativeFontHooks => true;

    public NativeFontHookProfile NativeFontHookProfile
        => new(
            NativeHookTarget.Export(
                "ldfile",
                "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z",
                0x4093C4,
                [0x48, 0x8B, 0xC4, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "shpload",
                "?shpload@@YAHPEB_WHPEAVAcDbDatabase@@_N00HHW4Charset@@W4FontPitch@FontUtils@PAL@AutoCAD@Autodesk@@W4FontFamily@4567@@Z",
                0x3F2BFC,
                [0x48, 0x8B, 0xC4, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D],
                maxPrologueSize: 64));
}
