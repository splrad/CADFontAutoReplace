using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2018 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2018Platform : ICadPlatform, INativeFontHookExportsProvider
{
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

    public string AcDbMTextExplodeFragmentsExport => "?explodeFragments@AcDbMText@@QEBAXP6AHPEAUAcDbMTextFragment@@PEAX@Z1PEAVAcGiWorldDraw@@@Z";

    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z";

    public uint? LdFileRva => 0x4093C4;

    public NativeFontHookProfile NativeFontHookProfile
        => new(
            NativeHookTarget.Export("AcGiTextStyle::loadStyleRec", AcGiTextStyleLoadStyleRecExport, 0x373AEC, [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0x25, 0xCE, 0xC2, 0x00, 0x48], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setFont", AcGiTextStyleSetFontExport, 0x373B54, [0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x40, 0x41], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setFileName", AcGiTextStyleSetFileNameExport, 0x373D38, [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0xD1, 0xCB, 0xC2, 0x00, 0x48], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setBigFontFileName", AcGiTextStyleSetBigFontFileNameExport, 0x373D14, [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0xF5, 0xCB, 0xC2, 0x00, 0x48], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::AcGiTextStyle(font,bigFont)", AcGiTextStyleFileNameCtorExport, 0x71EE84, [0x48, 0x8B, 0xC4, 0x48, 0x89, 0x48, 0x08, 0x41, 0x56, 0x48, 0x81, 0xEC, 0x90, 0x00, 0x00, 0x00], maxPrologueSize: 96),
            NativeHookTarget.Export("AcDbMText::explodeFragments", AcDbMTextExplodeFragmentsExport, 0x9B09A0, [0x48, 0x8B, 0xC1, 0x33, 0xC9, 0x48, 0x85, 0xC0, 0x74, 0x04, 0x48, 0x8B, 0x48, 0x10, 0xE9, 0xDD], maxPrologueSize: 64),
            NativeHookTarget.Export("ldfile", LdFileExport, 0x4093C4, [0x48, 0x8B, 0xC4, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D], maxPrologueSize: 64));

    public string BrandName => "AutoCAD";
    public string VersionName => "2018";
    public string AppName => "AFR-ACAD2018";
    public string DisplayName => "AutoCAD 2018";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R22.0";
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$";
    public string AcDbDllName => "acdb22.dll";
    public bool SupportsNativeFontHooks => true;
}
