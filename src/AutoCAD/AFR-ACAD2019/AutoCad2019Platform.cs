using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2019 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2019Platform : ICadPlatform, INativeFontHookExportsProvider
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

    public uint? LdFileRva => 0x44518;

    public NativeFontHookProfile NativeFontHookProfile
        => new(
            NativeHookTarget.Export("AcGiTextStyle::loadStyleRec", AcGiTextStyleLoadStyleRecExport, 0x4C580, [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0x19, 0xF3, 0xC6, 0x00, 0x48], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setFont", AcGiTextStyleSetFontExport, 0x4C298, [0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x40, 0x41], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setFileName", AcGiTextStyleSetFileNameExport, 0x4C560, [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0x41, 0xF3, 0xC6, 0x00, 0x48], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setBigFontFileName", AcGiTextStyleSetBigFontFileNameExport, 0x4C520, [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0xFF, 0x15, 0x81, 0xF3, 0xC6, 0x00, 0x48], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::AcGiTextStyle(font,bigFont)", AcGiTextStyleFileNameCtorExport, 0xC44D14, [0x48, 0x8B, 0xC4, 0x48, 0x89, 0x48, 0x08, 0x57, 0x48, 0x81, 0xEC, 0x90, 0x00, 0x00, 0x00, 0x48], maxPrologueSize: 96),
            NativeHookTarget.Export("AcDbMText::explodeFragments", AcDbMTextExplodeFragmentsExport, 0xBCFD20, [0x48, 0x8B, 0xC1, 0x33, 0xC9, 0x48, 0x85, 0xC0, 0x74, 0x04, 0x48, 0x8B, 0x48, 0x10, 0xE9, 0x7D], maxPrologueSize: 64),
            NativeHookTarget.Export("ldfile", LdFileExport, 0x44518, [0x48, 0x8B, 0xC4, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D], maxPrologueSize: 64));

    public string BrandName => "AutoCAD";
    public string VersionName => "2019";
    public string AppName => "AFR-ACAD2019";
    public string DisplayName => "AutoCAD 2019";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R23.0";
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$";
    public string AcDbDllName => "acdb23.dll";
    public bool SupportsNativeFontHooks => true;
}
