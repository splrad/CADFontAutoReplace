using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

internal interface INativeDecodeHookProfileProvider
{
    NativeDecodeHookProfile NativeDecodeHookProfile { get; }
}

internal interface INativeFontHookExportsProvider
{
    string AcGiTextStyleLoadStyleRecExport { get; }

    string AcGiTextStyleStyleNameExport { get; }

    string AcGiTextStyleFileNameExport { get; }

    string AcGiTextStyleBigFontFileNameExport { get; }

    string AcGiTextStyleIsVerticalExport { get; }

    string AcGiTextStyleSetVerticalExport { get; }

    string AcGiTextStyleSetFontExport { get; }

    string AcGiTextStyleSetFileNameExport { get; }

    string AcGiTextStyleSetBigFontFileNameExport { get; }

    string AcGiTextStyleFileNameCtorExport { get; }

    string AcDbMTextExplodeFragmentsExport { get; }
}

internal enum FilerCodePageResolverKind
{
    None,
    Export,
    VirtualRecord
}

internal sealed class NativeHookTarget
{
    private NativeHookTarget(
        string name,
        string? exportName,
        uint? rva,
        byte[] expectedPrefix,
        int minPrologueSize,
        int maxPrologueSize,
        int[]? signature,
        string? disabledReason)
    {
        Name = name;
        ExportName = exportName;
        Rva = rva;
        ExpectedPrefix = expectedPrefix;
        MinPrologueSize = minPrologueSize;
        MaxPrologueSize = maxPrologueSize;
        Signature = signature;
        DisabledReason = disabledReason;
    }

    public string Name { get; }

    public string? ExportName { get; }

    public uint? Rva { get; }

    public byte[] ExpectedPrefix { get; }

    public int MinPrologueSize { get; }

    public int MaxPrologueSize { get; }

    public int[]? Signature { get; }

    public string? DisabledReason { get; }

    public bool IsEnabled => DisabledReason is null;

    public static NativeHookTarget Export(
        string name,
        string exportName,
        uint rva,
        byte[] expectedPrefix,
        int minPrologueSize = 14,
        int maxPrologueSize = 32)
        => new(name, exportName, rva, expectedPrefix, minPrologueSize, maxPrologueSize, null, null);

    public static NativeHookTarget RvaOnly(
        string name,
        uint rva,
        byte[] expectedPrefix,
        int minPrologueSize = 14,
        int maxPrologueSize = 32)
        => new(name, null, rva, expectedPrefix, minPrologueSize, maxPrologueSize, null, null);

    public static NativeHookTarget Pattern(
        string name,
        int[] signature,
        byte[] expectedPrefix,
        int minPrologueSize = 14,
        int maxPrologueSize = 32)
        => new(name, null, null, expectedPrefix, minPrologueSize, maxPrologueSize, signature, null);

    public static NativeHookTarget Pattern(
        string name,
        uint rva,
        int[] signature,
        byte[] expectedPrefix,
        int minPrologueSize = 14,
        int maxPrologueSize = 32)
        => new(name, null, rva, expectedPrefix, minPrologueSize, maxPrologueSize, signature, null);

    public static NativeHookTarget Disabled(string name, string reason)
        => new(name, null, null, [], 0, 0, null, reason);
}

internal sealed class DwgFilerCodePageHookProfile
{
    public DwgFilerCodePageHookProfile(
        NativeHookTarget readStringAcString,
        NativeHookTarget readStringWideCharPointer,
        NativeHookTarget codePageIdIsDoubleByte,
        NativeHookTarget getFilerCodePageId,
        FilerCodePageResolverKind resolverKind,
        int virtualCodePageMethodOffset,
        int virtualCodePageRecordCodePageOffset,
        int filerBitOffsetOffset,
        int filerBufferPointerOffset,
        int filerByteOffsetOffset)
    {
        ReadStringAcString = readStringAcString;
        ReadStringWideCharPointer = readStringWideCharPointer;
        CodePageIdIsDoubleByte = codePageIdIsDoubleByte;
        GetFilerCodePageId = getFilerCodePageId;
        ResolverKind = resolverKind;
        VirtualCodePageMethodOffset = virtualCodePageMethodOffset;
        VirtualCodePageRecordCodePageOffset = virtualCodePageRecordCodePageOffset;
        FilerBitOffsetOffset = filerBitOffsetOffset;
        FilerBufferPointerOffset = filerBufferPointerOffset;
        FilerByteOffsetOffset = filerByteOffsetOffset;
    }

    public NativeHookTarget ReadStringAcString { get; }

    public NativeHookTarget ReadStringWideCharPointer { get; }

    public NativeHookTarget CodePageIdIsDoubleByte { get; }

    public NativeHookTarget GetFilerCodePageId { get; }

    public FilerCodePageResolverKind ResolverKind { get; }

    public int VirtualCodePageMethodOffset { get; }

    public int VirtualCodePageRecordCodePageOffset { get; }

    public int FilerBitOffsetOffset { get; }

    public int FilerBufferPointerOffset { get; }

    public int FilerByteOffsetOffset { get; }

    public bool HasInstallableReadStringHook
        => ReadStringAcString.IsEnabled || ReadStringWideCharPointer.IsEnabled;
}

internal sealed class DbTextDwgInFieldsHookProfile
{
    public DbTextDwgInFieldsHookProfile(
        NativeHookTarget dwgInFields,
        NativeHookTarget wideStringAssign,
        int impTextStringConstVtableOffset,
        int impTextStringPointerOffset,
        int impTextStringToWideCharMethodOffset,
        int impTextStringLengthMethodOffset,
        int textSourceVtableOffset,
        int textSourceBufferMethodOffset,
        int textSourceLengthMethodOffset,
        int textSourceMetadataMethodOffset,
        int textSourceReadCodeUnitMethodOffset,
        int textSourceReleaseMethodOffset)
    {
        DwgInFields = dwgInFields;
        WideStringAssign = wideStringAssign;
        ImpTextStringConstVtableOffset = impTextStringConstVtableOffset;
        ImpTextStringPointerOffset = impTextStringPointerOffset;
        ImpTextStringToWideCharMethodOffset = impTextStringToWideCharMethodOffset;
        ImpTextStringLengthMethodOffset = impTextStringLengthMethodOffset;
        TextSourceVtableOffset = textSourceVtableOffset;
        TextSourceBufferMethodOffset = textSourceBufferMethodOffset;
        TextSourceLengthMethodOffset = textSourceLengthMethodOffset;
        TextSourceMetadataMethodOffset = textSourceMetadataMethodOffset;
        TextSourceReadCodeUnitMethodOffset = textSourceReadCodeUnitMethodOffset;
        TextSourceReleaseMethodOffset = textSourceReleaseMethodOffset;
    }

    public NativeHookTarget DwgInFields { get; }

    public NativeHookTarget WideStringAssign { get; }

    public int ImpTextStringConstVtableOffset { get; }

    public int ImpTextStringPointerOffset { get; }

    public int ImpTextStringToWideCharMethodOffset { get; }

    public int ImpTextStringLengthMethodOffset { get; }

    public int TextSourceVtableOffset { get; }

    public int TextSourceBufferMethodOffset { get; }

    public int TextSourceLengthMethodOffset { get; }

    public int TextSourceMetadataMethodOffset { get; }

    public int TextSourceReadCodeUnitMethodOffset { get; }

    public int TextSourceReleaseMethodOffset { get; }
}

internal sealed class DbTextUpstreamDecodeHookProfile
{
    public DbTextUpstreamDecodeHookProfile(
        NativeHookTarget multiByteCifToWideChar,
        NativeHookTarget dTextFullInputProbe,
        NativeHookTarget utf16ToWideGetWideBuffer,
        NativeHookTarget mainDispatcher,
        NativeHookTarget parallelDispatcher)
    {
        MultiByteCifToWideChar = multiByteCifToWideChar;
        DTextFullInputProbe = dTextFullInputProbe;
        Utf16ToWideGetWideBuffer = utf16ToWideGetWideBuffer;
        MainDispatcher = mainDispatcher;
        ParallelDispatcher = parallelDispatcher;
    }

    public NativeHookTarget MultiByteCifToWideChar { get; }

    public NativeHookTarget DTextFullInputProbe { get; }

    public NativeHookTarget Utf16ToWideGetWideBuffer { get; }

    public NativeHookTarget MainDispatcher { get; }

    public NativeHookTarget ParallelDispatcher { get; }

    public bool HasInstallableHook
        => MultiByteCifToWideChar.IsEnabled
            || DTextFullInputProbe.IsEnabled
            || Utf16ToWideGetWideBuffer.IsEnabled
            || MainDispatcher.IsEnabled
            || ParallelDispatcher.IsEnabled;
}

internal sealed class TextEditorDbcsDecodeHookProfile
{
    public TextEditorDbcsDecodeHookProfile(NativeHookTarget readDoubleByteAnsi, NativeHookTarget multiByteToUnicodeAcString)
    {
        ReadDoubleByteAnsi = readDoubleByteAnsi;
        MultiByteToUnicodeAcString = multiByteToUnicodeAcString;
    }

    public NativeHookTarget ReadDoubleByteAnsi { get; }

    public NativeHookTarget MultiByteToUnicodeAcString { get; }

    public bool HasInstallableHook => ReadDoubleByteAnsi.IsEnabled || MultiByteToUnicodeAcString.IsEnabled;
}

internal sealed class CodePageFamilyHookProfile
{
    public CodePageFamilyHookProfile(NativeHookTarget target, int codePageIdFieldOffset)
    {
        Target = target;
        CodePageIdFieldOffset = codePageIdFieldOffset;
    }

    public NativeHookTarget Target { get; }

    public int CodePageIdFieldOffset { get; }
}

internal sealed class NativeDecodeHookProfile
{
    public NativeDecodeHookProfile(
        string platformName,
        string acDbDllName,
        string supportNote,
        DwgFilerCodePageHookProfile dwgFilerCodePage,
        DbTextDwgInFieldsHookProfile dbTextDwgInFields,
        DbTextUpstreamDecodeHookProfile upstreamDecode,
        TextEditorDbcsDecodeHookProfile textEditorDbcsDecode,
        CodePageFamilyHookProfile codePageFamily)
    {
        PlatformName = platformName;
        AcDbDllName = acDbDllName;
        SupportNote = supportNote;
        DwgFilerCodePage = dwgFilerCodePage;
        DbTextDwgInFields = dbTextDwgInFields;
        UpstreamDecode = upstreamDecode;
        TextEditorDbcsDecode = textEditorDbcsDecode;
        CodePageFamily = codePageFamily;
    }

    public string PlatformName { get; }

    public string AcDbDllName { get; }

    public string SupportNote { get; }

    public DwgFilerCodePageHookProfile DwgFilerCodePage { get; }

    public DbTextDwgInFieldsHookProfile DbTextDwgInFields { get; }

    public DbTextUpstreamDecodeHookProfile UpstreamDecode { get; }

    public TextEditorDbcsDecodeHookProfile TextEditorDbcsDecode { get; }

    public CodePageFamilyHookProfile CodePageFamily { get; }
}

internal static class NativeDecodeHookProfileResolver
{
    public static bool TryGetCurrent(string hookName, out NativeDecodeHookProfile profile)
    {
        profile = null!;
        var platform = PlatformManager.Platform;
        if (platform is not INativeDecodeHookProfileProvider provider)
        {
            DiagnosticLogger.Log("NativeDecodeHookProfile", $"{hookName}: 当前平台未提供 DBText native hook profile。");
            return false;
        }

        profile = provider.NativeDecodeHookProfile;
        if (!string.Equals(platform.AcDbDllName, profile.AcDbDllName, StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticLogger.Log("NativeDecodeHookProfile", $"{hookName}: profile DLL({profile.AcDbDllName}) 与平台 DLL({platform.AcDbDllName}) 不一致，跳过 DBText native hook。");
            return false;
        }

        return true;
    }
}

internal static class AutoCadNativeDecodeHookProfiles
{
    private static readonly byte[] ReadStringAcStringPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x18, 0x55, 0x56, 0x57,
        0x41, 0x56, 0x41, 0x57, 0x48, 0x8B, 0xEC, 0x48,
        0x83, 0xEC, 0x60
    ];

    private static readonly byte[] ReadStringWideCharPointerPrefix =
    [
        0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55,
        0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x00,
        0x01, 0x00, 0x00
    ];

    private static readonly byte[] DbTextDwgInFieldsPrefix =
    [
        0x40, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55,
        0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x00,
        0x02, 0x00, 0x00
    ];

    private static readonly byte[] WideStringAssignPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74,
        0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x20
    ];

    private static readonly byte[] MultiByteCifToWideCharPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x55, 0x56, 0x57,
        0x41, 0x54, 0x41, 0x55, 0x41, 0x56
    ];

    private static readonly byte[] DTextFullInputProbePrefix =
    [
        0x48, 0x85, 0xD2, 0x74, 0x3D, 0x48, 0x89, 0x5C,
        0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10
    ];

    private static readonly byte[] Utf16ToWideGetWideBufferPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74,
        0x24, 0x10, 0x57, 0x48, 0x83, 0xEC, 0x20
    ];

    private static readonly byte[] ReadDoubleByteAnsiPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74,
        0x24, 0x18, 0x57, 0x48, 0x83, 0xEC, 0x30
    ];

    private static readonly byte[] MultiByteToUnicodeAcStringPrefix =
    [
        0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48,
        0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48,
        0x89, 0x78, 0x20, 0x41, 0x56, 0x48, 0x83, 0xEC,
        0x30
    ];

    private static readonly byte[] CodePageFamilyPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C,
        0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57,
        0x48, 0x83, 0xEC, 0x20
    ];

    private static readonly byte[] DispatcherPrefix =
    [
        0x48, 0x89, 0x5C, 0x24, 0x20,
        0x55,
        0x56,
        0x57,
        0x41, 0x54,
        0x41, 0x55,
        0x41, 0x56,
        0x41, 0x57
    ];

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
        0x4C, 0x8B, 0xE9,
        0x49, 0x8B, 0x88, 0x30, 0x04, 0x00, 0x00,
        0x49, 0x8B, 0xF8,
        0x48, 0x8B, 0xDA
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
        0x45, 0x8B, 0xA8, 0x6C, 0x04, 0x00, 0x00,
        0x4C, 0x8B, 0xE1,
        0x49, 0x8B, 0x88, 0x30, 0x04, 0x00, 0x00,
        0x49, 0x8B, 0xF0,
        0x48, 0x8B, 0xFA
    ];

    private static readonly int[] CodePageFamilySignature =
    [
        0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C,
        0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57,
        0x48, 0x83, 0xEC, 0x20, 0x33, 0xED, 0x89, 0x11,
        0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xF0, 0x3F, 0x66, 0x89, 0x69, 0x04, 0x83, 0xCE,
        0xFF, 0x48, 0x89, 0x41, 0x08, 0x89, 0x71, 0x10,
        0x48, 0x8B, 0xF9, 0x89, 0x69, 0x14, 0x89, 0x71,
        0x18, 0x48, 0x83, 0xC1, 0x20
    ];

    public static NativeDecodeHookProfile CreateFullProfile(
        string platformName,
        string acDbDllName,
        string readStringAcStringExport,
        string? readStringWideCharPointerExport,
        string getFilerCodePageIdExport,
        string codePageIdIsDoubleByteExport,
        string multiByteCifToWideCharExport,
        string? utf16ToWideGetWideBufferExport,
        string readDoubleByteAnsiExport,
        string multiByteToUnicodeAcStringExport,
        string acutUpdStringName,
        uint readStringAcStringRva,
        uint? readStringWideCharPointerRva,
        uint getFilerCodePageIdRva,
        uint codePageIdIsDoubleByteRva,
        uint dbTextDwgInFieldsRva,
        uint? wideStringAssignRva,
        uint multiByteCifToWideCharRva,
        uint dTextFullInputProbeRva,
        uint readDoubleByteAnsiRva,
        uint multiByteToUnicodeAcStringRva,
        uint codePageFamilyRva,
        bool enableDispatcherPatterns,
        bool enableAcPalUtf16Probe,
        byte[]? readStringAcStringPrefix = null,
        byte[]? readStringWideCharPointerPrefix = null,
        byte[]? dbTextDwgInFieldsPrefix = null,
        byte[]? multiByteCifToWideCharPrefix = null,
        byte[]? readDoubleByteAnsiPrefix = null,
        uint? acPalUtf16ToWideGetWideBufferRva = null,
        byte[]? acPalUtf16ToWideGetWideBufferPrefix = null,
        bool readStringWideCharPointerUseRvaOnly = false,
        uint? mainDispatcherRva = null,
        uint? parallelDispatcherRva = null,
        int[]? mainDispatcherPattern = null,
        int[]? parallelDispatcherPattern = null)
        => new(
            platformName,
            acDbDllName,
            "baseline acdb profile with DBText strong-evidence hooks enabled",
            new DwgFilerCodePageHookProfile(
                NativeHookTarget.Export("AcDbMemoryDwgFiler::readString(AcString)", readStringAcStringExport, readStringAcStringRva, readStringAcStringPrefix ?? ReadStringAcStringPrefix, maxPrologueSize: 64),
                readStringWideCharPointerRva.HasValue
                    ? readStringWideCharPointerUseRvaOnly
                        ? NativeHookTarget.RvaOnly("AcDbMemoryDwgFiler::readString(wchar**)", readStringWideCharPointerRva.Value, readStringWideCharPointerPrefix ?? ReadStringWideCharPointerPrefix, maxPrologueSize: 64)
                        : !string.IsNullOrWhiteSpace(readStringWideCharPointerExport)
                            ? NativeHookTarget.Export("AcDbMemoryDwgFiler::readString(wchar**)", readStringWideCharPointerExport!, readStringWideCharPointerRva.Value, readStringWideCharPointerPrefix ?? ReadStringWideCharPointerPrefix, maxPrologueSize: 64)
                            : NativeHookTarget.Disabled("AcDbMemoryDwgFiler::readString(wchar**)", "当前版本未提供 readString(wchar**) 导出名，跳过该可选 readString 子 hook。")
                    : NativeHookTarget.Disabled("AcDbMemoryDwgFiler::readString(wchar**)", "当前版本缺少 readString(wchar**) 导出，跳过该可选 readString 子 hook。"),
                NativeHookTarget.Export("AcCodePage::CodePageIdIsDoubleByte", codePageIdIsDoubleByteExport, codePageIdIsDoubleByteRva, [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20], 6),
                NativeHookTarget.Export("acdbGetFilerCodePageId", getFilerCodePageIdExport, getFilerCodePageIdRva, [0x48, 0x83, 0xEC, 0x28], 4),
                FilerCodePageResolverKind.Export,
                virtualCodePageMethodOffset: 0,
                virtualCodePageRecordCodePageOffset: 0,
                filerBitOffsetOffset: 0x10,
                filerBufferPointerOffset: 0x30,
                filerByteOffsetOffset: 0x50),
            new DbTextDwgInFieldsHookProfile(
                NativeHookTarget.RvaOnly("AcDbImpText::dwgInFields", dbTextDwgInFieldsRva, dbTextDwgInFieldsPrefix ?? DbTextDwgInFieldsPrefix, maxPrologueSize: 64),
                wideStringAssignRva.HasValue
                    ? NativeHookTarget.RvaOnly(acutUpdStringName, wideStringAssignRva.Value, WideStringAssignPrefix, maxPrologueSize: 32)
                    : NativeHookTarget.Disabled(acutUpdStringName, "当前版本未静态确认唯一 RVA，跳过可选文本写入来源 hook。"),
                impTextStringConstVtableOffset: 0x560,
                impTextStringPointerOffset: 0x68,
                impTextStringToWideCharMethodOffset: 0x40,
                impTextStringLengthMethodOffset: 0x88,
                textSourceVtableOffset: 0xB8,
                textSourceBufferMethodOffset: 0xC0,
                textSourceLengthMethodOffset: 0xC8,
                textSourceMetadataMethodOffset: 0x188,
                textSourceReadCodeUnitMethodOffset: 0x280,
                textSourceReleaseMethodOffset: 0x290),
            new DbTextUpstreamDecodeHookProfile(
                NativeHookTarget.Export("MultiByteCIFToWideChar", multiByteCifToWideCharExport, multiByteCifToWideCharRva, multiByteCifToWideCharPrefix ?? MultiByteCifToWideCharPrefix, maxPrologueSize: 24),
                NativeHookTarget.RvaOnly("DText full-input multibyte decode probe", dTextFullInputProbeRva, DTextFullInputProbePrefix, DTextFullInputProbePrefix.Length, DTextFullInputProbePrefix.Length),
                enableAcPalUtf16Probe && !string.IsNullOrWhiteSpace(utf16ToWideGetWideBufferExport)
                    ? NativeHookTarget.Export("Utf16ToWideCharHelper::getWideBuffer", utf16ToWideGetWideBufferExport!, acPalUtf16ToWideGetWideBufferRva ?? 0x4F900, acPalUtf16ToWideGetWideBufferPrefix ?? Utf16ToWideGetWideBufferPrefix, maxPrologueSize: 24)
                    : NativeHookTarget.Disabled("Utf16ToWideCharHelper::getWideBuffer", "未提供当前 CAD 版本 AcPal.dll 基线，跳过可选 AcPal 子 hook。"),
                enableDispatcherPatterns
                    ? CreateDispatcherTarget("DBText main dispatcher", mainDispatcherRva, mainDispatcherPattern ?? MainDispatcherPattern)
                    : NativeHookTarget.Disabled("DBText main dispatcher", "当前版本未静态确认唯一 dispatcher 签名，跳过可选 dispatcher hook。"),
                enableDispatcherPatterns
                    ? CreateDispatcherTarget("DBText parallel dispatcher", parallelDispatcherRva, parallelDispatcherPattern ?? ParallelDispatcherPattern)
                    : NativeHookTarget.Disabled("DBText parallel dispatcher", "当前版本未静态确认唯一 dispatcher 签名，跳过可选 dispatcher hook。")),
            new TextEditorDbcsDecodeHookProfile(
                NativeHookTarget.Export("TextEditor::read_doublebyte", readDoubleByteAnsiExport, readDoubleByteAnsiRva, readDoubleByteAnsiPrefix ?? ReadDoubleByteAnsiPrefix, maxPrologueSize: 64),
                NativeHookTarget.Export("TextEditor::MultiByteToUnicode(AcString)", multiByteToUnicodeAcStringExport, multiByteToUnicodeAcStringRva, MultiByteToUnicodeAcStringPrefix, maxPrologueSize: 64)),
            new CodePageFamilyHookProfile(
                NativeHookTarget.Pattern("code-page family context", codePageFamilyRva, CodePageFamilySignature, CodePageFamilyPrefix, maxPrologueSize: 64),
                codePageIdFieldOffset: 0x46C));

    public static NativeDecodeHookProfile CreateFailClosedProfile(string platformName, string acDbDllName, string reason)
    {
        var disabledDwgFiler = new DwgFilerCodePageHookProfile(
            NativeHookTarget.Disabled("AcDbMemoryDwgFiler::readString(AcString)", reason),
            NativeHookTarget.Disabled("AcDbMemoryDwgFiler::readString(wchar**)", reason),
            NativeHookTarget.Disabled("AcCodePage::CodePageIdIsDoubleByte", reason),
            NativeHookTarget.Disabled("acdbGetFilerCodePageId", reason),
            FilerCodePageResolverKind.None,
            virtualCodePageMethodOffset: 0,
            virtualCodePageRecordCodePageOffset: 0,
            filerBitOffsetOffset: 0,
            filerBufferPointerOffset: 0,
            filerByteOffsetOffset: 0);

        return new NativeDecodeHookProfile(
            platformName,
            acDbDllName,
            reason,
            disabledDwgFiler,
            new DbTextDwgInFieldsHookProfile(
                NativeHookTarget.Disabled("AcDbImpText::dwgInFields", reason),
                NativeHookTarget.Disabled("acutUpdString(wchar_t const*, wchar_t**)", reason),
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            new DbTextUpstreamDecodeHookProfile(
                NativeHookTarget.Disabled("MultiByteCIFToWideChar", reason),
                NativeHookTarget.Disabled("DText full-input multibyte decode probe", reason),
                NativeHookTarget.Disabled("Utf16ToWideCharHelper::getWideBuffer", reason),
                NativeHookTarget.Disabled("DBText main dispatcher", reason),
                NativeHookTarget.Disabled("DBText parallel dispatcher", reason)),
            new TextEditorDbcsDecodeHookProfile(
                NativeHookTarget.Disabled("TextEditor::read_doublebyte", reason),
                NativeHookTarget.Disabled("TextEditor::MultiByteToUnicode(AcString)", reason)),
            new CodePageFamilyHookProfile(
                NativeHookTarget.Disabled("code-page family context", reason),
                codePageIdFieldOffset: 0x46C));
    }

    private static NativeHookTarget CreateDispatcherTarget(string name, uint? rva, int[] signature)
        => rva.HasValue
            ? NativeHookTarget.Pattern(name, rva.Value, signature, DispatcherPrefix, DispatcherPrefix.Length, maxPrologueSize: 32)
            : NativeHookTarget.Pattern(name, signature, DispatcherPrefix, DispatcherPrefix.Length, maxPrologueSize: 32);
}
