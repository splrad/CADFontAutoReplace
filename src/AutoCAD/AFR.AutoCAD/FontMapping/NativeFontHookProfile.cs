namespace AFR.FontMapping;

internal interface INativeFontHookExportsProvider
{
    NativeFontHookProfile NativeFontHookProfile { get; }

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

    string LdFileExport { get; }

    uint? LdFileRva { get; }
}

internal sealed class NativeFontHookProfile
{
    public NativeFontHookProfile(
        NativeHookTarget acGiTextStyleLoadStyleRec,
        NativeHookTarget acGiTextStyleSetFont,
        NativeHookTarget acGiTextStyleSetFileName,
        NativeHookTarget acGiTextStyleSetBigFontFileName,
        NativeHookTarget acGiTextStyleFileNameCtor,
        NativeHookTarget ldFile,
        NativeHookTarget? shpLoad = null,
        NativeHookTarget? mapFont = null)
    {
        AcGiTextStyleLoadStyleRec = acGiTextStyleLoadStyleRec;
        AcGiTextStyleSetFont = acGiTextStyleSetFont;
        AcGiTextStyleSetFileName = acGiTextStyleSetFileName;
        AcGiTextStyleSetBigFontFileName = acGiTextStyleSetBigFontFileName;
        AcGiTextStyleFileNameCtor = acGiTextStyleFileNameCtor;
        LdFile = ldFile;
        ShpLoad = shpLoad ?? NativeHookTarget.Disabled("shpload", "未提供已验证的 shpload TrueType 入口");
        MapFont = mapFont ?? NativeHookTarget.Disabled("mapFont", "未提供已验证的 mapFont 诊断入口");
    }

    public NativeHookTarget AcGiTextStyleLoadStyleRec { get; }

    public NativeHookTarget AcGiTextStyleSetFont { get; }

    public NativeHookTarget AcGiTextStyleSetFileName { get; }

    public NativeHookTarget AcGiTextStyleSetBigFontFileName { get; }

    public NativeHookTarget AcGiTextStyleFileNameCtor { get; }

    public NativeHookTarget LdFile { get; }

    public NativeHookTarget ShpLoad { get; }

    public NativeHookTarget MapFont { get; }
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
