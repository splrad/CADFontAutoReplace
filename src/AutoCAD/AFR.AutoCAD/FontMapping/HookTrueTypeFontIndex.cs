using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// Hook 侧进程级 TrueType 字体可用性索引。
/// <para>
/// 系统字体通过 DirectWrite 枚举；CAD 字体搜索路径中的 .ttf/.ttc/.otf 作为 Hook 侧文件兜底。
/// @TrueType 查询只按去掉 @ 后的基础字体判断，不再查询 GDI vertical face。
/// </para>
/// </summary>
internal static class HookTrueTypeFontIndex
{
    private const string Tag = "HookTrueTypeFontIndex";

    private static readonly object CacheLock = new();
    private static readonly HashSet<string> AvailableFonts = new(StringComparer.OrdinalIgnoreCase);

    private static volatile bool _initialized;
    private static volatile bool _directWriteAvailable;
    private static int _systemFontNameCount;
    private static int _fileFontNameCount;
    private static int _pathCount;

    internal static bool IsSystemIndexReady
    {
        get
        {
            EnsureInitialized();
            return _directWriteAvailable && _systemFontNameCount > 0;
        }
    }

    internal static void Initialize()
    {
        bool scanned = EnsureInitialized();
        DiagnosticLogger.Ok(
            Tag,
            "Initialize",
            scanned ? "Hook 侧 TrueType 字体索引已初始化" : "Hook 侧 TrueType 字体索引已复用",
            new Dictionary<string, object?> { ["scanned"] = scanned });
    }

    internal static bool IsAvailable(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        EnsureInitialized();
        return AvailableFonts.Contains(normalized);
    }

    internal static IReadOnlyCollection<string> GetAvailableFontNamesSnapshot()
    {
        EnsureInitialized();
        return AvailableFonts
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool EnsureInitialized()
    {
        if (_initialized)
            return false;

        lock (CacheLock)
        {
            if (_initialized)
                return false;

            ScanAvailableFonts();
            _initialized = true;
            return true;
        }
    }

    private static void ScanAvailableFonts()
    {
        bool directWriteAvailable = TryScanDirectWriteSystemFonts(AvailableFonts, out int systemFontNameCount);
        int beforeFileScan = AvailableFonts.Count;
        ScanCadTrueTypeFontFiles(AvailableFonts);

        _directWriteAvailable = directWriteAvailable;
        _systemFontNameCount = systemFontNameCount;
        _fileFontNameCount = Math.Max(0, AvailableFonts.Count - beforeFileScan);

        DiagnosticLogger.Ok(
            Tag,
            "ScanAvailableTrueTypeFonts",
            directWriteAvailable
                ? "Hook 侧 TrueType 字体索引已构建"
                : "Hook 侧 TrueType 字体索引已构建，DirectWrite 系统字体枚举不可用",
            new Dictionary<string, object?>
            {
                ["directWriteAvailable"] = directWriteAvailable,
                ["systemFontNameCount"] = _systemFontNameCount,
                ["fileFontNameCount"] = _fileFontNameCount,
                ["availableFonts"] = AvailableFonts.Count,
                ["pathCount"] = _pathCount
            });
    }

    private static bool TryScanDirectWriteSystemFonts(ISet<string> names, out int addedCount)
    {
        addedCount = 0;
        IDWriteFactory? factory = null;
        IDWriteFontCollection? collection = null;

        try
        {
            Guid factoryId = typeof(IDWriteFactory).GUID;
            int hr = DWriteCreateFactory(DWriteFactoryType.Shared, ref factoryId, out factory);
            if (Failed(hr) || factory == null)
            {
                LogDirectWriteFailure("DWriteCreateFactory", "DirectWrite Factory 创建失败", hr);
                return false;
            }

            hr = factory.GetSystemFontCollection(out collection, checkForUpdates: false);
            if (Failed(hr) || collection == null)
            {
                LogDirectWriteFailure("GetSystemFontCollection", "DirectWrite 系统字体集合获取失败", hr);
                return false;
            }

            int before = names.Count;
            uint familyCount = collection.GetFontFamilyCount();
            for (uint i = 0; i < familyCount; i++)
            {
                IDWriteFontFamily? family = null;
                IDWriteLocalizedStrings? familyNames = null;
                try
                {
                    hr = collection.GetFontFamily(i, out family);
                    if (Failed(hr) || family == null)
                        continue;

                    hr = family.GetFamilyNames(out familyNames);
                    if (Failed(hr) || familyNames == null)
                        continue;

                    AddLocalizedStrings(names, familyNames);
                }
                finally
                {
                    ReleaseComObject(familyNames);
                    ReleaseComObject(family);
                }
            }

            addedCount = Math.Max(0, names.Count - before);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Fail(
                Tag,
                "ScanDirectWriteSystemFonts",
                "DirectWrite 系统字体枚举失败",
                ex);
            return false;
        }
        finally
        {
            ReleaseComObject(collection);
            ReleaseComObject(factory);
        }
    }

    private static void ScanCadTrueTypeFontFiles(ISet<string> names)
    {
        var paths = CadEnvironmentSettings.GetAllFontSearchPaths();
        _pathCount = paths.Count;

        foreach (string dir in paths)
        {
            if (!Directory.Exists(dir))
                continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(file);
                    if (!IsTrueTypeExtension(ext))
                        continue;

                    AddFontAlias(names, Path.GetFileName(file));
                    AddFontAlias(names, Path.GetFileNameWithoutExtension(file));
                }
            }
            catch
            {
                // 字体目录不可读时跳过，后续按缺失字体处理。
            }
        }
    }

    private static void AddLocalizedStrings(ISet<string> names, IDWriteLocalizedStrings localizedStrings)
    {
        uint count = localizedStrings.GetCount();
        for (uint i = 0; i < count; i++)
        {
            int hr = localizedStrings.GetStringLength(i, out uint length);
            if (Failed(hr))
                continue;

            var buffer = new StringBuilder((int)length + 1);
            hr = localizedStrings.GetString(i, buffer, length + 1);
            if (Failed(hr))
                continue;

            AddFontAlias(names, buffer.ToString());
        }
    }

    private static void AddFontAlias(ISet<string> names, string? value)
    {
        string? alias = NormalizeAlias(value);
        if (!string.IsNullOrWhiteSpace(alias))
            names.Add(alias!);
    }

    private static string? NormalizeAlias(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return null;

        int hashIndex = trimmed.LastIndexOf('#');
        string name = hashIndex >= 0 && hashIndex + 1 < trimmed.Length
            ? trimmed[(hashIndex + 1)..]
            : trimmed;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static string NormalizeFontName(string fontName)
    {
        string trimmed = fontName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return string.Empty;

        string normalized;
        try
        {
            string fileName = Path.GetFileName(trimmed);
            normalized = string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
        }
        catch
        {
            normalized = trimmed;
        }

        normalized = normalized.Trim();
        if (normalized.Length > 1 && normalized[0] == '@')
            normalized = normalized.TrimStart('@');

        return normalized;
    }

    private static bool IsTrueTypeExtension(string extension)
        => extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase);

    private static bool Failed(int hr) => hr < 0;

    private static string FormatHResult(int hr) => $"0x{hr:X8}";

    private static void LogDirectWriteFailure(string operation, string message, int hr)
    {
        DiagnosticLogger.Fail(
            Tag,
            operation,
            message,
            fields: new Dictionary<string, object?> { ["hresult"] = FormatHResult(hr) });
    }

    private static void ReleaseComObject(object? value)
    {
        if (value == null)
            return;

        try
        {
            if (Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // 释放失败不影响字体判定路径，交给运行时回收。
        }
    }

    private enum DWriteFactoryType
    {
        Shared = 0
    }

    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(
        DWriteFactoryType factoryType,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IDWriteFactory factory);

    [ComImport]
    [Guid("B859EE5A-D838-4B5B-A2E8-1ADC7D93DB48")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig]
        int GetSystemFontCollection(
            out IDWriteFontCollection fontCollection,
            [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates);
    }

    [ComImport]
    [Guid("A84CEE02-3EEA-4EEE-A827-87C1A02A0FCC")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig]
        uint GetFontFamilyCount();

        [PreserveSig]
        int GetFontFamily(uint index, out IDWriteFontFamily fontFamily);
    }

    [ComImport]
    [Guid("DA20D8EF-812A-4C43-9802-62EC4ABD7ADD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        [PreserveSig]
        int GetFontCollection(out IntPtr fontCollection);

        [PreserveSig]
        uint GetFontCount();

        [PreserveSig]
        int GetFont(uint index, out IntPtr font);

        [PreserveSig]
        int GetFamilyNames(out IDWriteLocalizedStrings names);
    }

    [ComImport]
    [Guid("08256209-099A-4B34-B86D-C22B110E7771")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteLocalizedStrings
    {
        [PreserveSig]
        uint GetCount();

        [PreserveSig]
        int FindLocaleName(
            [MarshalAs(UnmanagedType.LPWStr)] string localeName,
            out uint index,
            [MarshalAs(UnmanagedType.Bool)] out bool exists);

        [PreserveSig]
        int GetLocaleNameLength(uint index, out uint length);

        [PreserveSig]
        int GetLocaleName(
            uint index,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder localeName,
            uint size);

        [PreserveSig]
        int GetStringLength(uint index, out uint length);

        [PreserveSig]
        int GetString(
            uint index,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder stringBuffer,
            uint size);
    }
}
