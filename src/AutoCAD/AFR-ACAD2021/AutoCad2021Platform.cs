using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2021 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2021Platform : ICadPlatform, INativeDecodeHookProfileProvider, INativeFontHookExportsProvider
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

    public uint? LdFileRva => null;

    public string BrandName => "AutoCAD";
    public string VersionName => "2021";
    public string AppName => "AFR-ACAD2021";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2021";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R24.0";  // AutoCAD 2021 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb24.dll";                  // AutoCAD 2021 的数据库 DLL
    public bool SupportsNativeFontHooks => true;

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFailClosedProfile(
            DisplayName,
            AcDbDllName,
            "2021 acdb 基线缺少 acdbGetFilerCodePageId 导出且虚表 resolver 未验证，DBText AI native hook fail closed。");
}
