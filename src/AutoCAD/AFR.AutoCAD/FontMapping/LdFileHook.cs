using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 处理 AutoCAD ldfile 阶段的 SHX 主字体/大字体运行时文件加载重定向。
/// </summary>
internal static class LdFileHook
{
    private const string Tag = "LdFileHook";

    private const int FontTypeRegular = 0;
    private const int FontTypeShape = 2;
    private const int FontTypeBigFont = 4;
    private const int TrueTypeBypassSampleLimit = 16;

    private static NativeInlineHook<LdFileDelegate>? _hook;
    private static LdFileDelegate? _hookDelegate;
    private static readonly ConcurrentDictionary<string, IntPtr> NativeStringCache =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (string Replacement, int FontType)> RedirectLog =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> RedirectLogSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> TrueTypeBypassLogSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _hitCount;
    private static long _redirectCount;

    [ThreadStatic] private static bool _inHook;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LdFileDelegate(IntPtr fileName, int param2, IntPtr db, IntPtr desc);

    internal static bool IsInstalled => _hook?.IsInstalled == true;

    internal static (long HitCount, long RedirectCount) GetCountersSnapshot()
        => (Interlocked.Read(ref _hitCount), Interlocked.Read(ref _redirectCount));

    internal static IReadOnlyDictionary<string, (string Replacement, int FontType)> GetRawRedirectLog()
        => RedirectLog;

    internal static void ClearRegisteredRedirects()
    {
        RedirectLog.Clear();
        RedirectLogSeen.Clear();
        TrueTypeBypassLogSeen.Clear();
    }

    internal static void ClearRegisteredRedirectsForDocument(IntPtr _)
    {
        RedirectLog.Clear();
        RedirectLogSeen.Clear();
    }

    internal static void ClearTransientRegisteredRedirects()
    {
        RedirectLogSeen.Clear();
        TrueTypeBypassLogSeen.Clear();
    }

    internal static IntPtr GetDatabaseScope(Database? db)
    {
        try
        {
            return db?.UnmanagedObject ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    internal static void Install()
    {
        if (IsInstalled)
        {
            DiagnosticLogger.Skip(Tag, "Install", "ldfile Hook 已安装，跳过重复安装");
            return;
        }

        DiagnosticLogger.Start(Tag, "Install", "开始安装 ldfile 字体加载 Hook");
        if (PlatformManager.Platform is not INativeFontHookExportsProvider exports)
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "当前平台未提供 ldfile Hook 导出定义，跳过字体加载桥接",
                new Dictionary<string, object?> { ["platform"] = PlatformManager.Platform.DisplayName });
            return;
        }

        IntPtr module = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "AcDb 模块未加载，跳过 ldfile 字体加载 Hook",
                new Dictionary<string, object?> { ["module"] = PlatformManager.Platform.AcDbDllName });
            return;
        }

        NativeHookTarget target = exports.NativeFontHookProfile.LdFile;
        if (!TryGetExportAddress(module, target, out IntPtr address, out uint resolvedRva))
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "ldfile 入口未通过强校验，跳过字体加载桥接",
                new Dictionary<string, object?> { ["target"] = target.Name });
            return;
        }

        _hookDelegate = HookHandler;
        _hook = new NativeInlineHook<LdFileDelegate>(Tag, target.Name, target.Rva ?? resolvedRva);
        _hook.InstallAtAddress(
            address,
            resolvedRva,
            _hookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);

        if (IsInstalled)
        {
            DiagnosticLogger.Ok(
                Tag,
                "Install",
                "ldfile 字体加载 Hook 安装成功",
                new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["rva"] = $"0x{resolvedRva:X}"
                });
        }
        else
        {
            DiagnosticLogger.Fail(
                Tag,
                "Install",
                "ldfile 字体加载 Hook 安装未成功",
                fields: new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["rva"] = $"0x{resolvedRva:X}"
                });
        }
    }

    internal static void Uninstall()
    {
        bool installedBefore = IsInstalled;
        if (installedBefore)
        {
            DiagnosticLogger.Start(
                Tag,
                "Uninstall",
                "开始卸载 ldfile 字体加载 Hook",
                new Dictionary<string, object?>
                {
                    ["hitCount"] = Interlocked.Read(ref _hitCount),
                    ["redirects"] = Interlocked.Read(ref _redirectCount)
                });
        }
        else
        {
            DiagnosticLogger.Skip(Tag, "Uninstall", "ldfile Hook 未安装，跳过卸载");
        }

        _hook?.Uninstall();
        _hook = null;
        _hookDelegate = null;
        ClearRegisteredRedirects();
        foreach (IntPtr ptr in NativeStringCache.Values)
        {
            try { Marshal.FreeHGlobal(ptr); } catch { }
        }

        NativeStringCache.Clear();
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _redirectCount, 0);
        if (installedBefore)
            DiagnosticLogger.Ok(Tag, "Uninstall", "ldfile 字体加载 Hook 卸载完成");
    }

    private static int HookHandler(IntPtr fileName, int param2, IntPtr db, IntPtr desc)
    {
        var trampoline = _hook?.TrampolineDelegate;
        if (trampoline == null)
            return -1;

        if (_inHook)
            return trampoline(fileName, param2, db, desc);

        Interlocked.Increment(ref _hitCount);
        if (param2 == FontTypeShape)
            return trampoline(fileName, param2, db, desc);

        _inHook = true;
        try
        {
            string original = NormalizeLoadName(ReadFontName(fileName));
            if (string.IsNullOrWhiteSpace(original))
                return trampoline(fileName, param2, db, desc);

            if (IsKnownAvailableLoadFont(original))
                return trampoline(fileName, param2, db, desc);

            if (ShouldBypassTrueTypeRequest(original, param2))
                return trampoline(fileName, param2, db, desc);

            if (!IsShxLoadRequest(original, param2))
                return trampoline(fileName, param2, db, desc);

            if (!TryResolveMissingShxFont(
                    original,
                    param2,
                    out string replacement,
                    out FontRedirectKind kind,
                    out string reason))
            {
                return trampoline(fileName, param2, db, desc);
            }

            string normalized = NormalizeShxName(original.TrimStart('@'));
            bool hasAtPrefix = original.Length > 1 && original[0] == '@';
            if (!hasAtPrefix && string.Equals(normalized, replacement, StringComparison.OrdinalIgnoreCase))
                return trampoline(fileName, param2, db, desc);

            Interlocked.Increment(ref _redirectCount);
            RedirectLog[normalized] = (replacement, param2);
            RedirectLog[original] = (replacement, param2);
            FontRuntimeMappingStore.RecordRuntimeMapping(
                "文件级",
                string.Empty,
                original,
                hasAtPrefix ? normalized : string.Empty,
                GetFontTypeText(kind),
                replacement,
                "LdFileHook",
                "已映射");

            string logKey = $"{original}|{replacement}|{param2}|{reason}";
            if (RedirectLogSeen.TryAdd(logKey, 0))
            {
                DiagnosticLogger.Ok(
                    Tag,
                    "HookHandler",
                    "执行 SHX 字体加载重定向",
                    new Dictionary<string, object?>
                    {
                        ["kind"] = kind.ToString(),
                        ["original"] = original,
                        ["replacement"] = replacement,
                        ["request"] = normalized,
                        ["param2"] = param2,
                        ["reason"] = reason,
                        ["dbScope"] = FormatPointer(db)
                    });
            }

            IntPtr replacementPtr = NativeStringCache.GetOrAdd(
                replacement,
                static name => Marshal.StringToHGlobalUni(name));
            return trampoline(replacementPtr, param2, db, desc);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Fail(Tag, "HookHandler", "ldfile Hook 异常", ex);
            return trampoline(fileName, param2, db, desc);
        }
        finally
        {
            _inHook = false;
        }
    }

    private static bool TryGetExportAddress(
        IntPtr module,
        NativeHookTarget target,
        out IntPtr address,
        out uint rva)
    {
        address = IntPtr.Zero;
        rva = 0;

        if (!target.IsEnabled || string.IsNullOrWhiteSpace(target.ExportName))
        {
            DiagnosticLogger.Skip(
                Tag,
                "ResolveExport",
                "Hook 目标未启用",
                new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["reason"] = target.DisabledReason ?? "缺少导出符号"
                });
            return false;
        }

        address = NativeInlineHookInterop.GetProcAddress(module, target.ExportName!);
        if (address == IntPtr.Zero)
        {
            DiagnosticLogger.Skip(
                Tag,
                "ResolveExport",
                "Hook 导出未找到",
                new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["exportName"] = target.ExportName
                });
            return false;
        }

        long delta = address.ToInt64() - module.ToInt64();
        if (delta <= 0 || delta > uint.MaxValue)
        {
            DiagnosticLogger.Fail(
                Tag,
                "ResolveExport",
                "Hook 导出 RVA 解析失败",
                fields: new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["address"] = $"0x{address.ToInt64():X}"
                });
            address = IntPtr.Zero;
            return false;
        }

        rva = (uint)delta;
        if (target.Rva.HasValue && target.Rva.Value != rva)
        {
            DiagnosticLogger.Skip(
                Tag,
                "ResolveExport",
                "Hook 导出 RVA 不匹配",
                new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["expectedRva"] = $"0x{target.Rva.Value:X}",
                    ["actualRva"] = $"0x{rva:X}"
                });
            address = IntPtr.Zero;
            rva = 0;
            return false;
        }

        DiagnosticLogger.Ok(
            Tag,
            "ResolveExport",
            "Hook 导出解析成功",
            new Dictionary<string, object?>
            {
                ["target"] = target.Name,
                ["rva"] = $"0x{rva:X}"
            });
        return true;
    }

    private static bool IsKnownAvailableLoadFont(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return true;

        if (FontAvailabilityIndex.IsExactKnownAvailableFont(fontName))
            return true;

        if (!Path.HasExtension(fontName)
            && FontAvailabilityIndex.IsExactKnownAvailableFont(fontName + ".shx"))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldBypassTrueTypeRequest(string fontName, int param2)
    {
        if (FontDetector.IsTrueTypeFontFile(fontName))
        {
            LogTrueTypeBypass(fontName, param2, "TrueType 字体文件");
            return true;
        }

        string baseName = fontName.TrimStart('@');
        if (string.IsNullOrWhiteSpace(baseName) || Path.HasExtension(baseName))
            return false;

        if (FontDetector.IsSystemFont(baseName))
        {
            LogTrueTypeBypass(fontName, param2, "系统 TrueType 字族");
            return true;
        }

        if (FontDetector.IsSystemFontIndexReady)
            return false;

        if (HasKnownShxCandidate(baseName))
            return false;

        LogTrueTypeBypass(fontName, param2, "系统 TrueType 索引未就绪，无扩展名请求保守放行");
        return true;
    }

    private static bool HasKnownShxCandidate(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        string shxName = NormalizeShxName(fontName);
        return FontAvailabilityIndex.IsExactKnownAvailableFont(shxName);
    }

    private static void LogTrueTypeBypass(string fontName, int param2, string reason)
    {
        if (TrueTypeBypassLogSeen.Count >= TrueTypeBypassSampleLimit)
            return;

        string logKey = $"{fontName}|{param2}|{reason}";
        if (!TrueTypeBypassLogSeen.TryAdd(logKey, 0))
            return;

        DiagnosticLogger.Skip(
            Tag,
            "TrueTypeBypass",
            "TrueType 请求已放行，LdFileHook 不处理",
            new Dictionary<string, object?>
            {
                ["original"] = fontName,
                ["param2"] = param2,
                ["reason"] = reason,
                ["systemFontIndexReady"] = FontDetector.IsSystemFontIndexReady
            });
    }

    private static bool IsShxLoadRequest(string fontName, int param2)
    {
        return param2 == FontTypeRegular
               || param2 == FontTypeBigFont
               || fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveMissingShxFont(
        string fontName,
        int param2,
        out string replacement,
        out FontRedirectKind kind,
        out string reason)
    {
        replacement = string.Empty;
        kind = param2 == FontTypeBigFont ? FontRedirectKind.ShxBigFont : FontRedirectKind.ShxMain;
        reason = string.Empty;

        bool hasAtPrefix = fontName.Length > 1 && fontName[0] == '@';
        string lookup = hasAtPrefix ? fontName.TrimStart('@') : fontName;
        string baseShx = NormalizeShxName(lookup);

        if (hasAtPrefix
            && TryUseAvailableShx(baseShx, kind, out replacement))
        {
            reason = "基础 SHX 可用";
            return true;
        }

        if (FontRedirectResolver.TryResolveConfiguredReplacement(kind, out replacement))
        {
            reason = "配置 SHX 兜底";
            return true;
        }

        if (kind == FontRedirectKind.ShxBigFont
            && TryFindCachedShx(expectBigFont: true, out replacement))
        {
            reason = "已知大字体兜底";
            return true;
        }

        if (kind == FontRedirectKind.ShxMain
            && TryFindCachedShx(expectBigFont: false, out replacement))
        {
            reason = "已知主字体兜底";
            return true;
        }

        return false;
    }

    private static bool TryUseAvailableShx(
        string shxName,
        FontRedirectKind kind,
        out string replacement)
    {
        replacement = string.Empty;
        if (!FontAvailabilityIndex.IsExactKnownAvailableFont(shxName))
            return false;

        if (!FontAvailabilityIndex.TryGetKnownShxFontKind(shxName, out bool isBigFont))
            return false;

        if (kind == FontRedirectKind.ShxBigFont && !isBigFont)
            return false;
        if (kind == FontRedirectKind.ShxMain && isBigFont)
            return false;

        replacement = shxName;
        return true;
    }

    private static bool TryFindCachedShx(bool expectBigFont, out string replacement)
    {
        foreach (var pair in FontManager.FontCache)
        {
            if (pair.Value != expectBigFont)
                continue;
            if (!FontAvailabilityIndex.IsExactKnownAvailableFont(pair.Key))
                continue;

            replacement = pair.Key;
            return true;
        }

        replacement = string.Empty;
        return false;
    }

    private static string NormalizeLoadName(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return string.Empty;

        string trimmed = fontName.Trim();
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
    }

    private static string NormalizeShxName(string fontName)
    {
        string normalized = NormalizeLoadName(fontName).TrimStart('@');
        return normalized.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".shx";
    }

    private static string GetFontTypeText(FontRedirectKind kind)
        => kind == FontRedirectKind.ShxBigFont ? "SHX大字体" : "SHX主字体";

    private static string ReadFontName(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return string.Empty;

        try
        {
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatPointer(IntPtr value)
        => value == IntPtr.Zero ? "0x0" : $"0x{value.ToInt64():X}";

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
