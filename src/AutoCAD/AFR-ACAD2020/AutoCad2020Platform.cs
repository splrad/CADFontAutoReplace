using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2020 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2020Platform : ICadPlatform, INativeDecodeHookProfileProvider, INativeFontHookExportsProvider
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

    public uint? LdFileRva => 0x47BF0;

    public string BrandName => "AutoCAD";
    public string VersionName => "2020";
    public string AppName => "AFR-ACAD2020";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2020";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R23.1";  // AutoCAD 2020 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb23.dll";                  // AutoCAD 2020 的数据库 DLL
    public bool SupportsNativeFontHooks => true;

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFailClosedProfile(
            DisplayName,
            AcDbDllName,
            "2020 acdb 基线可经 RTTI 定位 AcDbImpText::dwgInFields，但缺少 readString 与 acdbGetFilerCodePageId 导出且 resolver 未验证，DBText AI native hook fail closed。");
}
