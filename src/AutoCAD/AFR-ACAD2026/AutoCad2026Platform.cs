using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2026 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2026Platform : ICadPlatform, INativeDecodeHookProfileProvider, INativeFontHookExportsProvider
{
    private const string ReadStringAcStringExport = "?readString@AcDbMemoryDwgFiler@@UEAA?AW4ErrorStatus@Acad@@AEAVAcString@@@Z";
    private const string ReadStringWideCharPointerExport = "?readString@AcDbMemoryDwgFiler@@UEAA?AW4ErrorStatus@Acad@@PEAPEA_W@Z";
    private const string GetFilerCodePageIdExport = "?acdbGetFilerCodePageId@@YA?AW4code_page_id@@PEAVAcDbDwgFiler@@@Z";
    private const string CodePageIdIsDoubleByteExport = "?CodePageIdIsDoubleByte@AcCodePage@@SA_NW4code_page_id@@@Z";
    private const string MultiByteCifToWideCharExport = "?MultiByteCIFToWideChar@@YAHW4code_page_id@@W4MB2Uni@@PEBDHPEA_WH@Z";
    private const string Utf16ToWideGetWideBufferExport = "?getWideBuffer@Utf16ToWideCharHelper@UnicodeConvert@PAL@AutoCAD@Autodesk@@QEAA_NPEA_WAEA_K@Z";
    private const string ReadDoubleByteAnsiExport = "?read_doublebyte@TextEditor@@CA_NPEBDAEA_WW4code_page_id@@@Z";
    private const string MultiByteToUnicodeAcStringExport = "?MultiByteToUnicode@TextEditor@@SA_NPEBDHW4code_page_id@@AEAVAcString@@@Z";
    private const string AcutUpdStringName = "acutUpdString(wchar_t const*, wchar_t**)";

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

    public uint? LdFileRva => 0xD87AC;

    public string BrandName => "AutoCAD";
    public string VersionName => "2026";
    public string AppName => "AFR-ACAD2026";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2026";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R25.1";  // AutoCAD 2026 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb25.dll";                  // AutoCAD 2026 的数据库 DLL
    public bool SupportsNativeFontHooks => true;

    public NativeDecodeHookProfile NativeDecodeHookProfile
        => AutoCadNativeDecodeHookProfiles.CreateFullProfile(
            DisplayName,
            AcDbDllName,
            readStringAcStringExport: ReadStringAcStringExport,
            readStringWideCharPointerExport: ReadStringWideCharPointerExport,
            getFilerCodePageIdExport: GetFilerCodePageIdExport,
            codePageIdIsDoubleByteExport: CodePageIdIsDoubleByteExport,
            multiByteCifToWideCharExport: MultiByteCifToWideCharExport,
            utf16ToWideGetWideBufferExport: Utf16ToWideGetWideBufferExport,
            readDoubleByteAnsiExport: ReadDoubleByteAnsiExport,
            multiByteToUnicodeAcStringExport: MultiByteToUnicodeAcStringExport,
            acutUpdStringName: AcutUpdStringName,
            readStringAcStringRva: 0x93E70,
            readStringWideCharPointerRva: 0x93F60,
            getFilerCodePageIdRva: 0x4478E0,
            codePageIdIsDoubleByteRva: 0xBCF310,
            dbTextDwgInFieldsRva: 0x33690,
            wideStringAssignRva: 0x5EFEC,
            multiByteCifToWideCharRva: 0xD71A8,
            dTextFullInputProbeRva: 0x6DE5B0,
            readDoubleByteAnsiRva: 0x6E01F8,
            multiByteToUnicodeAcStringRva: 0x6B9F04,
            codePageFamilyRva: 0x6DCDC0,
            enableDispatcherPatterns: true,
            enableAcPalUtf16Probe: true,
            acPalUtf16ToWideGetWideBufferRva: 0x54D50,
            mainDispatcherRva: 0x6DEB90,
            parallelDispatcherRva: 0x6DE674);
}
