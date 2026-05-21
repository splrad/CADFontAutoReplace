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

    string AcDbMTextExplodeFragmentsExport { get; }

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
        NativeHookTarget acDbMTextExplodeFragments,
        NativeHookTarget ldFile)
    {
        AcGiTextStyleLoadStyleRec = acGiTextStyleLoadStyleRec;
        AcGiTextStyleSetFont = acGiTextStyleSetFont;
        AcGiTextStyleSetFileName = acGiTextStyleSetFileName;
        AcGiTextStyleSetBigFontFileName = acGiTextStyleSetBigFontFileName;
        AcGiTextStyleFileNameCtor = acGiTextStyleFileNameCtor;
        AcDbMTextExplodeFragments = acDbMTextExplodeFragments;
        LdFile = ldFile;
    }

    public NativeHookTarget AcGiTextStyleLoadStyleRec { get; }

    public NativeHookTarget AcGiTextStyleSetFont { get; }

    public NativeHookTarget AcGiTextStyleSetFileName { get; }

    public NativeHookTarget AcGiTextStyleSetBigFontFileName { get; }

    public NativeHookTarget AcGiTextStyleFileNameCtor { get; }

    public NativeHookTarget AcDbMTextExplodeFragments { get; }

    public NativeHookTarget LdFile { get; }
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
