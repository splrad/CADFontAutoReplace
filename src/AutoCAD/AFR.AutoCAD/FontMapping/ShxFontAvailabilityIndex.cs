using System.Diagnostics;
using System.IO;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 进程级 SHX 字体可用性共享索引。
/// <para>
/// 统一服务样式表检测、Hook 运行时映射和 UI 字体列表。
/// 只有能识别主/大字体类型的 SHX 才会进入主字体/大字体快照和类型匹配候选。
/// </para>
/// </summary>
internal static class ShxFontAvailabilityIndex
{
    private const string Tag = "ShxFontAvailabilityIndex";
    private const int ConflictSampleLimit = 8;

#if NET9_0_OR_GREATER
    private static readonly System.Threading.Lock CacheLock = new();
#else
    private static readonly object CacheLock = new();
#endif
    private static readonly Dictionary<string, ShxFontEntry> ShxFonts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ConflictNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SizeConflictNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ShxConflictSample> ConflictSamples = [];
    private static volatile bool _initialized;
    private static int _pathCount;
    private static int _primaryPathCount;

    private sealed record ShxFontEntry(
        string FileName,
        string FullPath,
        long Length,
        bool? IsBigFont,
        int SourceRank,
        int SourceOrder);

    private sealed record ShxConflictSample(
        string FileName,
        string PreferredPath,
        long PreferredLength,
        int PreferredRank,
        string CandidatePath,
        long CandidateLength,
        int CandidateRank);

    internal static void Initialize()
    {
        bool scanned = EnsureInitialized();
        DiagnosticLogger.Ok(
            Tag,
            "Initialize",
            scanned ? "SHX 字体共享索引已初始化" : "SHX 字体共享索引已复用",
            new Dictionary<string, object?> { ["scanned"] = scanned });
    }

    internal static bool IsAvailableWithAtFallback(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        EnsureInitialized();

        string fileName = NormalizeFileName(fontName);
        return TryGetShxEntry(fileName, allowAtFallback: true, out _);
    }

    internal static bool IsExactAvailable(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        EnsureInitialized();

        string fileName = NormalizeFileName(fontName);
        return TryGetExactShxEntry(fileName, out _);
    }

    internal static bool TryGetKind(string fontName, out bool isBigFont)
    {
        isBigFont = false;
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        EnsureInitialized();

        string fileName = NormalizeFileName(fontName);

        if (!TryGetShxEntry(fileName, allowAtFallback: true, out ShxFontEntry? entry)
            || entry == null
            || !entry.IsBigFont.HasValue)
        {
            return false;
        }

        isBigFont = entry.IsBigFont.Value;
        return true;
    }

    internal static IReadOnlyCollection<string> GetAllFontNamesSnapshot()
    {
        EnsureInitialized();
        lock (CacheLock)
        {
            return
            [
                .. ShxFonts.Keys
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            ];
        }
    }

    internal static IReadOnlyCollection<string> GetMainFontNamesSnapshot()
        => GetFontNamesByKind(isBigFont: false);

    internal static IReadOnlyCollection<string> GetBigFontNamesSnapshot()
        => GetFontNamesByKind(isBigFont: true);

    internal static bool TryFindFontByKind(bool expectBigFont, out string fontName)
    {
        EnsureInitialized();
        lock (CacheLock)
        {
            foreach (var entry in ShxFonts.Values
                         .Where(item => item.IsBigFont == expectBigFont)
                         .OrderBy(item => item.SourceRank)
                         .ThenBy(item => item.SourceOrder)
                         .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
            {
                fontName = entry.FileName;
                return true;
            }
        }

        fontName = string.Empty;
        return false;
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
        var paths = CadEnvironmentSettings.GetAllFontSearchPaths();
        _pathCount = paths.Count;

        for (int i = 0; i < paths.Count; i++)
        {
            int sourceRank = GetSourceRank(paths[i]);
            if (IsPrimarySource(sourceRank))
                _primaryPathCount++;

            ScanDirectory(paths[i], sourceRank, i);
        }

        DiagnosticLogger.Ok(
            Tag,
            "ScanAvailableShxFonts",
            "SHX 字体共享索引已构建",
            new Dictionary<string, object?>
            {
                ["shxCount"] = ShxFonts.Count,
                ["conflictNameCount"] = ConflictNames.Count,
                ["sizeConflictNameCount"] = SizeConflictNames.Count,
                ["pathCount"] = _pathCount,
                ["primaryPathCount"] = _primaryPathCount
            });

        LogConflictSamples();
    }

    private static void ScanDirectory(string dir, int sourceRank, int sourceOrder)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".shx", StringComparison.OrdinalIgnoreCase))
                {
                    AddShxFont(file, sourceRank, sourceOrder);
                }
            }
        }
        catch
        {
            // 字体目录不可读时跳过，后续按缺失字体处理。
        }
    }

    private static void AddShxFont(string filePath, int sourceRank, int sourceOrder)
    {
        string fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        long length = GetFileLength(filePath);
        bool? isBigFont = ShxFontAnalyzer.IsBigFont(filePath);
        var candidate = new ShxFontEntry(fileName, filePath, length, isBigFont, sourceRank, sourceOrder);

        if (!ShxFonts.TryGetValue(fileName, out ShxFontEntry? existing))
        {
            ShxFonts[fileName] = candidate;
            return;
        }

        TrackConflict(existing, candidate);
        if (!IsPreferred(candidate, existing))
            return;

        ShxFonts[fileName] = candidate;
    }

    private static IReadOnlyCollection<string> GetFontNamesByKind(bool isBigFont)
    {
        EnsureInitialized();
        lock (CacheLock)
        {
            return
            [
                .. ShxFonts.Values
                    .Where(entry => entry.IsBigFont == isBigFont)
                    .Select(entry => entry.FileName)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            ];
        }
    }

    private static void TrackConflict(ShxFontEntry existing, ShxFontEntry candidate)
    {
        if (string.Equals(existing.FullPath, candidate.FullPath, StringComparison.OrdinalIgnoreCase)
            && existing.Length == candidate.Length)
        {
            return;
        }

        ConflictNames.Add(existing.FileName);
        bool sizeDiffers = existing.Length != candidate.Length;
        if (!sizeDiffers)
            return;

        SizeConflictNames.Add(existing.FileName);
        if (ConflictSamples.Count >= ConflictSampleLimit)
            return;

        ShxFontEntry preferred = IsPreferred(candidate, existing) ? candidate : existing;
        ShxFontEntry other = ReferenceEquals(preferred, candidate) ? existing : candidate;
        ConflictSamples.Add(new ShxConflictSample(
            existing.FileName,
            preferred.FullPath,
            preferred.Length,
            preferred.SourceRank,
            other.FullPath,
            other.Length,
            other.SourceRank));
    }

    private static bool IsPreferred(ShxFontEntry candidate, ShxFontEntry existing)
    {
        if (candidate.SourceRank != existing.SourceRank)
            return candidate.SourceRank < existing.SourceRank;

        return candidate.SourceOrder < existing.SourceOrder;
    }

    private static bool TryGetShxEntry(
        string fileName,
        bool allowAtFallback,
        out ShxFontEntry? entry)
    {
        if (TryGetExactShxEntry(fileName, out entry))
            return true;

        if (!allowAtFallback || fileName.Length <= 1 || fileName[0] != '@')
            return false;

        // fileName[0] == '@' 已确认，直接切片，无需 TrimStart 扫描
        return TryGetExactShxEntry(fileName[1..], out entry);
    }

    private static bool TryGetExactShxEntry(string fileName, out ShxFontEntry? entry)
    {
        entry = null;
        string normalized = NormalizeShxKey(fileName);
        return normalized.Length > 0
               && ShxFonts.TryGetValue(normalized, out entry);
    }

    private static string NormalizeShxKey(string fontName)
    {
        string fileName = NormalizeFileName(fontName);
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        if (fileName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            return fileName;

        if (Path.HasExtension(fileName))
            return string.Empty;

        return fileName + ".shx";
    }

    private static string NormalizeFileName(string fontName)
    {
        string trimmed = fontName.Trim();
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

    private static long GetFileLength(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return -1;
        }
    }

    private static int GetSourceRank(string directory)
    {
        if (IsProcessFontsDirectory(directory))
            return 0;

        if (IsFontsDirectory(directory)
            && directory.Contains("AutoCAD", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (IsFontsDirectory(directory))
            return 2;

        return 3;
    }

    private static bool IsPrimarySource(int sourceRank) => sourceRank <= 1;

    private static bool IsProcessFontsDirectory(string directory)
    {
        string? processFonts = GetProcessFontsDirectory();
        return processFonts != null
               && string.Equals(
                   NormalizeDirectoryPath(directory),
                   NormalizeDirectoryPath(processFonts),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetProcessFontsDirectory()
    {
        try
        {
            string? processPath = Process.GetCurrentProcess().MainModule?.FileName;
            string? processDirectory = string.IsNullOrEmpty(processPath)
                ? null
                : Path.GetDirectoryName(processPath);
            return string.IsNullOrEmpty(processDirectory)
                ? null
                : Path.Combine(processDirectory, "Fonts");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFontsDirectory(string directory)
    {
        string normalized = NormalizeDirectoryPath(directory);
        string leaf = Path.GetFileName(normalized);
        return leaf.Equals("fonts", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string directory)
        => directory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void LogConflictSamples()
    {
        foreach (var sample in ConflictSamples)
        {
            DiagnosticLogger.Skip(
                Tag,
                "ShxNameConflict",
                "检测到同名 SHX 文件大小不一致，已按来源优先级选择首选项",
                new Dictionary<string, object?>
                {
                    ["fileName"] = sample.FileName,
                    ["preferredPath"] = sample.PreferredPath,
                    ["preferredLength"] = sample.PreferredLength,
                    ["preferredRank"] = sample.PreferredRank,
                    ["candidatePath"] = sample.CandidatePath,
                    ["candidateLength"] = sample.CandidateLength,
                    ["candidateRank"] = sample.CandidateRank
                });
        }
    }
}
