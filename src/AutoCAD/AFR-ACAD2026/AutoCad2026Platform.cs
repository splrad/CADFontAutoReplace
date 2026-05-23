using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2026 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2026Platform : ICadPlatform, INativeFontHookExportsProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2026";
    public string AppName => "AFR-ACAD2026";
    public string DisplayName => "AutoCAD 2026";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R25.1";
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$";
    public string AcDbDllName => "acdb25.dll";
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
    public uint? LdFileRva => 0xD87AC;

    public NativeFontHookProfile NativeFontHookProfile
        => new(
            NativeHookTarget.Export(
                "AcGiTextStyle::loadStyleRec",
                AcGiTextStyleLoadStyleRecExport,
                0x11EC58,
                [0x48, 0x8B, 0x41, 0x08, 0x48, 0x8B, 0x48, 0x08, 0xE9, 0x03, 0x00, 0x00, 0x00, 0xCC, 0xCC, 0xCC],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "AcGiTextStyle::setFont",
                AcGiTextStyleSetFontExport,
                0x577C4,
                [0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "AcGiTextStyle::setFileName",
                AcGiTextStyleSetFileNameExport,
                0x57518,
                [0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xFA, 0x48, 0x8B, 0xD9],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "AcGiTextStyle::setBigFontFileName",
                AcGiTextStyleSetBigFontFileNameExport,
                0x574DC,
                [0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0x48, 0x8B, 0xF9],
                maxPrologueSize: 64),
            NativeHookTarget.Export(
                "AcGiTextStyle::AcGiTextStyle(font,bigFont)",
                AcGiTextStyleFileNameCtorExport,
                0xB94498,
                [0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10, 0x48, 0x89, 0x68, 0x18, 0x48, 0x89, 0x70, 0x20, 0x48],
                maxPrologueSize: 96),
            NativeHookTarget.Export(
                "ldfile",
                LdFileExport,
                0xD87AC,
                [0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D, 0xAC],
                maxPrologueSize: 64));
}
