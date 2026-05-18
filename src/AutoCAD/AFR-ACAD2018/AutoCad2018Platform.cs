using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2018 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称、ldfile 导出符号等版本特定信息。
/// </summary>
internal sealed class AutoCad2018Platform : ICadPlatform, INativeDecodeHookProfileProvider
{
    public string BrandName => "AutoCAD";
    public string VersionName => "2018";
    public string AppName => "AFR-ACAD2018";
    public string DisplayName => "AutoCAD 2018";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R22.0";
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$";
    public string AcDbDllName => "acdb22.dll";
    public string LdFileExport => "?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z";
    public int PrologueSize => 21;
    public bool SupportsLdFileHook => true;

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFailClosedProfile(
            DisplayName,
            AcDbDllName,
            "2018 acdb 基线可经 RTTI 定位 AcDbImpText::dwgInFields，但缺少 readString 与 acdbGetFilerCodePageId 导出且 resolver 未验证，DBText AI native hook fail closed。");
}
