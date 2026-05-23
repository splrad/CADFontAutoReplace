using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2020 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2020Platform : ICadPlatform, INativeFontHookExportsProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2020";
    public string AppName => "AFR-ACAD2020";
    public string DisplayName => "AutoCAD 2020";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R23.1";
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$";
    public string AcDbDllName => "acdb23.dll";
    public bool SupportsNativeFontHooks => true;

    public string AcGiTextStyleLoadStyleRecExport => "?loadStyleRec@AcGiTextStyle@@UEBAHPEAVAcDbDatabase@@@Z";
    public string AcGiTextStyleStyleNameExport => "?styleName@AcGiTextStyle@@UEBAPEB_WXZ";
    public string AcGiTextStyleFileNameExport => "?fileName@AcGiTextStyle@@UEBAPEB_WXZ";
    public string AcGiTextStyleBigFontFileNameExport => "?bigFontFileName@AcGiTextStyle@@UEBAPEB_WXZ";
    public string AcGiTextStyleIsVerticalExport => "?isVertical@AcGiTextStyle@@UEBA_NXZ";
    public string AcGiTextStyleSetVerticalExport => "?setVertical@AcGiTextStyle@@UEAAX_N@Z";
    public string AcGiTextStyleSetFontExport => "?setFont@AcGiTextStyle@@UEAA?AW4ErrorStatus@Acad@@PEB_W_N1W4Charset@@W4FontPitch@FontUtils@PAL@AutoCAD@Autodesk@@W4FontFamily@6789@@Z";
    public string AcGiTextStyleSetFileNameExport => "?setFileName@AcGiTextStyle@@UEAAXPEB_W@Z";
    public string AcGiTextStyleSetBigFontFileNameExport => "?setBigFontFileName@AcGiTextStyle@@UEAAXPEB_W@Z";
    public string AcGiTextStyleFileNameCtorExport => "??0AcGiTextStyle@@QEAA@PEB_W0NNNN_N111110@Z";
    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z";
    public uint? LdFileRva => 0x47BF0;

    public NativeFontHookProfile NativeFontHookProfile
        => new(
            NativeHookTarget.Export(
                "AcGiTextStyle::loadStyleRec",
                AcGiTextStyleLoadStyleRecExport,
                0x4B708,
                [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0xD1, 0x95, 0xCA, 0x00, 0x48],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "AcGiTextStyle::setFont",
                AcGiTextStyleSetFontExport,
                0x815C0,
                [0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x40, 0x41],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "AcGiTextStyle::setFileName",
                AcGiTextStyleSetFileNameExport,
                0x816D8,
                [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0x31, 0x37, 0xC7, 0x00, 0x48],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "AcGiTextStyle::setBigFontFileName",
                AcGiTextStyleSetBigFontFileNameExport,
                0x81598,
                [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0x71, 0x38, 0xC7, 0x00, 0x48],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "AcGiTextStyle::AcGiTextStyle(font,bigFont)",
                AcGiTextStyleFileNameCtorExport,
                0xAF9A9C,
                [0x48, 0x8B, 0xC4, 0x48, 0x89, 0x48, 0x08, 0x57, 0x48, 0x81, 0xEC, 0x90, 0x00, 0x00, 0x00, 0x48],
                maxPrologueSize: 96),
            NativeHookTarget.Export(
                "ldfile",
                LdFileExport,
                0x47BF0,
                [0x48, 0x8B, 0xC4, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D],
                maxPrologueSize: 64));
}
