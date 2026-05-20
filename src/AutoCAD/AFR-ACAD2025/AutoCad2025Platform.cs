using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2025 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2025Platform : ICadPlatform, INativeDecodeHookProfileProvider, INativeFontHookExportsProvider
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

    public uint? LdFileRva => 0xD2988;

    public NativeFontHookProfile NativeFontHookProfile
        => new(
            NativeHookTarget.Export("AcGiTextStyle::loadStyleRec", AcGiTextStyleLoadStyleRecExport, 0x116798, [0x48, 0x8B, 0x41, 0x08, 0x48, 0x8B, 0x48, 0x08, 0xE9, 0x03, 0x00, 0x00, 0x00, 0xCC, 0xCC, 0xCC], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setFont", AcGiTextStyleSetFontExport, 0x18B28, [0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setFileName", AcGiTextStyleSetFileNameExport, 0x19454, [0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xFA, 0x48, 0x8B, 0xD9], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::setBigFontFileName", AcGiTextStyleSetBigFontFileNameExport, 0x19484, [0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xDA, 0x48, 0x8B, 0xF9], maxPrologueSize: 64),
            NativeHookTarget.Export("AcGiTextStyle::AcGiTextStyle(font,bigFont)", AcGiTextStyleFileNameCtorExport, 0xB857C0, [0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10, 0x48, 0x89, 0x68, 0x18, 0x48, 0x89, 0x70, 0x20, 0x48], maxPrologueSize: 96),
            NativeHookTarget.Export("AcDbMText::explodeFragments", AcDbMTextExplodeFragmentsExport, 0xB09804, [0x48, 0x8B, 0xC1, 0x33, 0xC9, 0x48, 0x85, 0xC0, 0x74, 0x04, 0x48, 0x8B, 0x48, 0x08, 0xE9, 0x7D], maxPrologueSize: 64),
            NativeHookTarget.Export("ldfile", LdFileExport, 0xD2988, [0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D, 0xAC], maxPrologueSize: 64));

    public string BrandName => "AutoCAD";
    public string VersionName => "2025";
    public string AppName => "AFR-ACAD2025";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2025";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R25.0";  // AutoCAD 2025 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb25.dll";                  // AutoCAD 2025 的数据库 DLL
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
            readStringAcStringRva: 0x8C738,
            readStringWideCharPointerRva: 0x8C830,
            getFilerCodePageIdRva: 0x43B100,
            codePageIdIsDoubleByteRva: 0xBC02EC,
            dbTextDwgInFieldsRva: 0x49910,
            wideStringAssignRva: 0x4EF44,
            multiByteCifToWideCharRva: 0x12965C,
            dTextFullInputProbeRva: 0x6D1660,
            readDoubleByteAnsiRva: 0x6D32A4,
            multiByteToUnicodeAcStringRva: 0x6ACF18,
            codePageFamilyRva: 0x6CFE6C,
            enableDispatcherPatterns: true,
            enableAcPalUtf16Probe: true,
            acPalUtf16ToWideGetWideBufferRva: 0x4F900,
            mainDispatcherRva: 0x6D1C40,
            parallelDispatcherRva: 0x6D1724);
}
