using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2027 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2027Platform : ICadPlatform, INativeFontHookExportsProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2027";
    public string AppName => "AFR-ACAD2027";
    public string DisplayName => "AutoCAD 2027";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R26.0";
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$";
    public string AcDbDllName => "acdb26.dll";
    public bool SupportsNativeFontHooks => true;

    public NativeFontHookProfile NativeFontHookProfile
        => new(
            NativeHookTarget.Export(
                "ldfile",
                "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z",
                0xA375C,
                [0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D, 0xAC],
                maxPrologueSize: 64));
}
