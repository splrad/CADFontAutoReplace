using AFR.Abstractions;
using AFR.FontMapping;

namespace AFR;

/// <summary>
/// AutoCAD 2027 版本的平台常量定义。
/// 包含注册表路径、acdb DLL 名称等版本特定信息。
/// </summary>
internal sealed class AutoCad2027Platform : ICadPlatform, INativeDecodeHookProfileProvider, INativeFontHookExportsProvider
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

    private static readonly int[] MainDispatcherPattern =
    [
        0x48, 0x89, 0x5C, 0x24, 0x20,
        0x55,
        0x56,
        0x57,
        0x41, 0x54,
        0x41, 0x55,
        0x41, 0x56,
        0x41, 0x57,
        0x48, 0x8B, 0xEC,
        0x48, 0x83, 0xEC, 0x40,
        0x48, 0x8B, 0x05, -1, -1, -1, -1,
        0x48, 0x33, 0xC4,
        0x48, 0x89, 0x45, 0xF8,
        0x45, 0x8B, 0xA0, 0x6C, 0x04, 0x00, 0x00,
        0x4C, 0x8B, 0xF9,
        0x49, 0x8B, 0x88, 0x30, 0x04, 0x00, 0x00,
        0x49, 0x8B, 0xF0,
        0x48, 0x8B, 0xFA
    ];

    private static readonly int[] ParallelDispatcherPattern =
    [
        0x48, 0x89, 0x5C, 0x24, 0x20,
        0x55,
        0x56,
        0x57,
        0x41, 0x54,
        0x41, 0x55,
        0x41, 0x56,
        0x41, 0x57,
        0x48, 0x8B, 0xEC,
        0x48, 0x83, 0xEC, 0x40,
        0x48, 0x8B, 0x05, -1, -1, -1, -1,
        0x48, 0x33, 0xC4,
        0x48, 0x89, 0x45, 0xF8,
        0x45, 0x8B, 0xB8, 0x6C, 0x04, 0x00, 0x00,
        0x4C, 0x8B, 0xE1,
        0x49, 0x8B, 0x88, 0x30, 0x04, 0x00, 0x00,
        0x49, 0x8B, 0xF0,
        0x48, 0x8B, 0xDA
    ];

    public string BrandName => "AutoCAD";
    public string VersionName => "2027";
    public string AppName => "AFR-ACAD2027";                    // 注册表中的应用名称
    public string DisplayName => "AutoCAD 2027";
    public string RegistryBasePath => @"Software\Autodesk\AutoCAD\R26.0";  // AutoCAD 2027 的注册表基路径
    public string RegistryKeyPattern => @"^ACAD-[A-Za-z0-9]+:[A-Za-z0-9]+$"; // 匹配配置文件子键的正则
    public string AcDbDllName => "acdb26.dll";                  // AutoCAD 2027 的数据库 DLL
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
            readStringAcStringRva: 0x4EF70,
            readStringWideCharPointerRva: 0x18D174,
            getFilerCodePageIdRva: 0x4579A4,
            codePageIdIsDoubleByteRva: 0xBF2344,
            dbTextDwgInFieldsRva: 0x2E740,
            wideStringAssignRva: 0x54C18,
            multiByteCifToWideCharRva: 0x140220,
            dTextFullInputProbeRva: 0x6E90DC,
            readDoubleByteAnsiRva: 0x6EAD14,
            multiByteToUnicodeAcStringRva: 0x6C4A0C,
            codePageFamilyRva: 0x6E79A0,
            enableDispatcherPatterns: true,
            enableAcPalUtf16Probe: true,
            readStringAcStringPrefix: [0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x10, 0x01, 0x00, 0x00],
            readStringWideCharPointerPrefix: [0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20],
            dbTextDwgInFieldsPrefix: [0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x30, 0x02, 0x00, 0x00],
            multiByteCifToWideCharPrefix: [0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55],
            acPalUtf16ToWideGetWideBufferRva: 0x5D430,
            readStringWideCharPointerUseRvaOnly: true,
            mainDispatcherRva: 0x6E91A0,
            parallelDispatcherRva: 0x6E96BC,
            mainDispatcherPattern: MainDispatcherPattern,
            parallelDispatcherPattern: ParallelDispatcherPattern);
}
