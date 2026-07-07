namespace AFR.FontMapping;

internal interface INativeFontHookExportsProvider
{
    NativeFontHookProfile NativeFontHookProfile { get; }
}

internal sealed class NativeFontHookProfile(
    NativeHookTarget ldFile,
    NativeHookTarget? shpLoad = null)
{
    public NativeHookTarget LdFile { get; } = ldFile;

    public NativeHookTarget ShpLoad { get; } =
        shpLoad ?? NativeHookTarget.Disabled("shpload", "未提供已验证的 shpload TrueType 入口");
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
        string? disabledReason)
    {
        Name = name;
        ExportName = exportName;
        Rva = rva;
        ExpectedPrefix = expectedPrefix;
        MinPrologueSize = minPrologueSize;
        MaxPrologueSize = maxPrologueSize;
        DisabledReason = disabledReason;
    }

    public string Name { get; }

    public string? ExportName { get; }

    public uint? Rva { get; }

    public byte[] ExpectedPrefix { get; }

    public int MinPrologueSize { get; }

    public int MaxPrologueSize { get; }

    public string? DisabledReason { get; }

    public bool IsEnabled => DisabledReason is null;

    public static NativeHookTarget Export(
        string name,
        string exportName,
        uint rva,
        byte[] expectedPrefix,
        int minPrologueSize = 14,
        int maxPrologueSize = 32)
        => new(name, exportName, rva, expectedPrefix, minPrologueSize, maxPrologueSize, null);

    public static NativeHookTarget Disabled(string name, string reason)
        => new(name, null, null, [], 0, 0, reason);
}
