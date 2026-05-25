using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 进程级 TrueType 字体可用性共享索引。
/// <para>
/// 系统字体通过 DirectWrite 枚举；CAD 字体搜索路径中的 .ttf/.ttc/.otf 作为文件兜底。
/// @TrueType 基础字体可用性仍按去掉 @ 后的基础字体判断；配置字体是否可用于 @TrueType
/// 由配置刷新阶段的 GDI vertical face 探测缓存决定，Hook 和样式表热路径只读缓存。
/// </para>
/// </summary>
internal static class TrueTypeFontAvailabilityIndex
{
    private const string Tag = "TrueTypeFontAvailabilityIndex";
    private const string SourceConfigured = "Configured";
    private const string SourceFallbackPreferred = "FallbackPreferred";
    private const string SourceFallbackCandidate = "FallbackCandidate";
    private const string SourceUnavailable = "Unavailable";
    private const string PreferredAtFallbackFont = "宋体";
    private const byte DefaultCharset = 1;
    private const int LfFaceSize = 32;
    private const int LfFullFaceSize = 64;

    private static readonly object CacheLock = new();
    private static readonly object AtResolutionLock = new();
    private static readonly HashSet<string> AvailableFonts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> VerticalFaceProbeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] AtFallbackCandidates =
    [
        PreferredAtFallbackFont,
        "SimSun",
        "NSimSun",
        "Microsoft YaHei",
        "Microsoft JhengHei",
        "Malgun Gothic",
        "Meiryo",
        "MS Gothic"
    ];

    private static volatile bool _initialized;
    private static volatile bool _directWriteAvailable;
    private static volatile AtTrueTypeResolution _atTrueTypeResolution = AtTrueTypeResolution.Unavailable;
    private static int _systemFontNameCount;
    private static int _fileFontNameCount;
    private static int _pathCount;

    private sealed record AtTrueTypeResolution(
        string ConfiguredBaseFont,
        string ResolvedAtBaseFont,
        bool ConfiguredSupportsAt,
        string ResolutionSource)
    {
        internal static AtTrueTypeResolution Unavailable { get; } =
            new(string.Empty, string.Empty, false, SourceUnavailable);

        internal bool IsAvailable => !string.IsNullOrWhiteSpace(ResolvedAtBaseFont);
    }

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
            scanned ? "TrueType 字体共享索引已初始化" : "TrueType 字体共享索引已复用",
            new Dictionary<string, object?> { ["scanned"] = scanned });
    }

    internal static void RefreshAtTrueTypeResolution(string configuredTrueTypeFont)
    {
        EnsureInitialized();

        string configuredBase = NormalizeAtBaseFontName(configuredTrueTypeFont);
        bool configuredSupportsAt = false;
        AtTrueTypeResolution resolution = AtTrueTypeResolution.Unavailable with
        {
            ConfiguredBaseFont = configuredBase
        };

        lock (AtResolutionLock)
        {
            foreach (var candidate in EnumerateAtResolutionCandidates(configuredBase))
            {
                bool isConfigured = string.Equals(candidate.FontName, configuredBase, StringComparison.OrdinalIgnoreCase);
                if (!IsAvailable(candidate.FontName))
                    continue;

                bool supportsAt = ProbeVerticalFaceAvailableCached(candidate.FontName);
                if (isConfigured)
                    configuredSupportsAt = supportsAt;

                if (!supportsAt)
                    continue;

                resolution = new AtTrueTypeResolution(
                    configuredBase,
                    candidate.FontName,
                    configuredSupportsAt,
                    candidate.Source);
                break;
            }

            if (!resolution.IsAvailable)
            {
                resolution = new AtTrueTypeResolution(
                    configuredBase,
                    string.Empty,
                    configuredSupportsAt,
                    SourceUnavailable);
            }

            _atTrueTypeResolution = resolution;
        }

        LogAtTrueTypeResolution(resolution);
    }

    internal static bool TryGetResolvedAtTrueTypeFont(out string baseFont, out string source)
    {
        AtTrueTypeResolution resolution = _atTrueTypeResolution;
        baseFont = resolution.ResolvedAtBaseFont;
        source = resolution.ResolutionSource;
        return resolution.IsAvailable;
    }

    internal static bool IsConfiguredTrueTypeAtCapable
        => _atTrueTypeResolution.ConfiguredSupportsAt;

    internal static bool IsVerticalTrueTypeAvailable(string baseFontName)
    {
        string normalized = NormalizeAtBaseFontName(baseFontName);
        return normalized.Length > 0
               && VerticalFaceProbeCache.TryGetValue(normalized, out bool available)
               && available;
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

    private static IEnumerable<(string FontName, string Source)> EnumerateAtResolutionCandidates(string configuredBase)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configuredBase)
            && seen.Add(configuredBase))
        {
            yield return (configuredBase, SourceConfigured);
        }

        foreach (string candidate in AtFallbackCandidates)
        {
            string normalized = NormalizeAtBaseFontName(candidate);
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                continue;

            string source = string.Equals(normalized, PreferredAtFallbackFont, StringComparison.OrdinalIgnoreCase)
                ? SourceFallbackPreferred
                : SourceFallbackCandidate;
            yield return (normalized, source);
        }
    }

    private static void LogAtTrueTypeResolution(AtTrueTypeResolution resolution)
    {
        var fields = new Dictionary<string, object?>
        {
            ["configuredTrueTypeFont"] = resolution.ConfiguredBaseFont,
            ["configuredSupportsAt"] = resolution.ConfiguredSupportsAt,
            ["resolvedAtTrueTypeFont"] = resolution.ResolvedAtBaseFont,
            ["resolutionSource"] = resolution.ResolutionSource
        };

        if (resolution.IsAvailable)
        {
            DiagnosticLogger.Ok(
                Tag,
                "RefreshAtTrueTypeResolution",
                resolution.ConfiguredSupportsAt
                    ? "@TrueType 配置字体支持 @face"
                    : "@TrueType 配置字体不支持 @face，已使用系统兜底",
                fields);
        }
        else
        {
            DiagnosticLogger.Fail(
                Tag,
                "RefreshAtTrueTypeResolution",
                "@TrueType 未找到可用的 @face 兜底字体",
                fields: fields);
        }
    }

    private static bool ProbeVerticalFaceAvailableCached(string baseFontName)
    {
        string normalized = NormalizeAtBaseFontName(baseFontName);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return VerticalFaceProbeCache.GetOrAdd(normalized, static name => ProbeVerticalFaceAvailable("@" + name));
    }

    private static bool ProbeVerticalFaceAvailable(string verticalFaceName)
    {
        bool matched = false;
        IntPtr hdc = IntPtr.Zero;
        try
        {
            hdc = CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return false;

            var logFont = new LOGFONTW
            {
                lfCharSet = DefaultCharset,
                lfFaceName = verticalFaceName
            };

            FontEnumProc callback = (ref ENUMLOGFONTEXW lpelfe, IntPtr _, uint __, IntPtr ___) =>
            {
                string enumerated = NormalizeFaceName(lpelfe.elfLogFont.lfFaceName);
                if (!string.Equals(enumerated, verticalFaceName, StringComparison.OrdinalIgnoreCase))
                    return 1;

                matched = true;
                return 0;
            };

            _ = EnumFontFamiliesExW(hdc, ref logFont, callback, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Fail(
                Tag,
                "ProbeVerticalFaceAvailable",
                "GDI vertical face 查询失败",
                ex,
                new Dictionary<string, object?> { ["faceName"] = verticalFaceName });
            return false;
        }
        finally
        {
            if (hdc != IntPtr.Zero)
                DeleteDC(hdc);
        }

        return matched;
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
                ? "TrueType 字体共享索引已构建"
                : "TrueType 字体共享索引已构建，DirectWrite 系统字体枚举不可用",
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

    private static string NormalizeAtBaseFontName(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        if (normalized.Length == 0)
            return string.Empty;

        try
        {
            string extension = Path.GetExtension(normalized);
            if (IsTrueTypeExtension(extension))
                normalized = Path.GetFileNameWithoutExtension(normalized);
        }
        catch
        {
            // 保留原名称继续尝试。
        }

        return normalized.TrimStart('@').Trim();
    }

    private static string NormalizeFaceName(string faceName)
    {
        if (string.IsNullOrWhiteSpace(faceName))
            return string.Empty;

        string trimmed = faceName.Trim();
        try
        {
            string fileName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
        }
        catch
        {
            return trimmed;
        }
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

    private delegate int FontEnumProc(
        ref ENUMLOGFONTEXW lpelfe,
        IntPtr lpntme,
        uint fontType,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LfFaceSize)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUMLOGFONTEXW
    {
        public LOGFONTW elfLogFont;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LfFullFaceSize)]
        public string elfFullName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LfFullFaceSize)]
        public string elfStyle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LfFullFaceSize)]
        public string elfScript;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesExW(
        IntPtr hdc,
        ref LOGFONTW lpLogfont,
        FontEnumProc lpProc,
        IntPtr lParam,
        uint dwFlags);

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
