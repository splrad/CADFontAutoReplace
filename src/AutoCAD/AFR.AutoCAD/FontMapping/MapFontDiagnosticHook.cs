#if DEBUG
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// Debug-only diagnostic hook for AutoCAD mapFont. It compares the upstream TrueType path with ldfile/shpload.
/// </summary>
internal static class MapFontDiagnosticHook
{
    private const string Tag = "MapFontDiag";
    private const int MaxSampleRecords = 96;
    private const int MaxDocumentSamples = 16;
    private const int MaxNameChars = 260;
    private const long FirstHitLogLimit = 16;
    private const long FirstEarlyRegisterLogLimit = 16;

    private static NativeInlineHook<MapFontDelegate>? _hook;
    private static MapFontDelegate? _hookDelegate;
    private static readonly ConcurrentDictionary<string, SampleRecord> Samples =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> EarlyRegisterLogSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _hitCount;
    private static long _redirectCount;
    private static long _earlyRegisterCount;
    private static long _styleScopeHitCount;
    private static long _mTextScopeHitCount;
    private static long _sampleSequence;
    private static long _sampleOverflowCount;

    [ThreadStatic] private static bool _inHook;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MapFontDelegate(IntPtr name, IntPtr desc, IntPtr db);

    internal sealed record CounterSnapshot(
        long HitCount,
        long RedirectCount,
        long EarlyRegisterCount,
        long StyleScopeHitCount,
        long MTextScopeHitCount,
        long SampleSequence,
        long SampleOverflowCount);

    private sealed record SampleRecord(
        string Name,
        string Desc,
        string Db,
        bool StyleScope,
        bool MTextScope,
        bool Redirected,
        string RedirectReplacement,
        long FirstSequence,
        long LastSequence,
        long Count,
        int LastResult);

    private readonly record struct NativeStringValue(string Text, string Display);

    private sealed record EarlyRegistration(
        string OriginalFont,
        string ReplacementFont,
        FontRedirectKind Kind,
        string Reason);

    internal static bool IsInstalled => _hook?.IsInstalled == true;

    internal static CounterSnapshot GetCountersSnapshot()
        => new(
            Interlocked.Read(ref _hitCount),
            Interlocked.Read(ref _redirectCount),
            Interlocked.Read(ref _earlyRegisterCount),
            Interlocked.Read(ref _styleScopeHitCount),
            Interlocked.Read(ref _mTextScopeHitCount),
            Interlocked.Read(ref _sampleSequence),
            Interlocked.Read(ref _sampleOverflowCount));

    internal static void Install()
    {
        if (IsInstalled)
        {
            DiagnosticLogger.Skip(Tag, "Install", "mapFont 诊断 Hook 已安装，跳过重复安装");
            return;
        }

        DiagnosticLogger.Start(Tag, "Install", "开始安装可选 mapFont 诊断 Hook");

        if (PlatformManager.Platform is not INativeFontHookExportsProvider exports)
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "当前平台未提供 native Hook profile，跳过 mapFont 诊断",
                new Dictionary<string, object?> { ["platform"] = PlatformManager.Platform.DisplayName });
            return;
        }

        IntPtr module = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "AcDb 模块未加载，跳过 mapFont 诊断",
                new Dictionary<string, object?> { ["module"] = PlatformManager.Platform.AcDbDllName });
            return;
        }

        NativeHookTarget target = exports.NativeFontHookProfile.MapFont;
        if (!TryGetExportAddress(module, target, out IntPtr address, out uint rva))
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "mapFont 入口未通过强校验，跳过诊断 Hook",
                new Dictionary<string, object?> { ["target"] = target.Name });
            return;
        }

        ResetDiagnostics();
        _hookDelegate = HookHandler;
        _hook = new NativeInlineHook<MapFontDelegate>(Tag, target.Name, target.Rva ?? rva);
        _hook.InstallAtAddress(
            address,
            rva,
            _hookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
        if (IsInstalled)
            DiagnosticLogger.Ok(Tag, "Install", "mapFont 诊断 Hook 安装成功", new Dictionary<string, object?> { ["target"] = target.Name, ["rva"] = $"0x{rva:X}" });
        else
            DiagnosticLogger.Fail(Tag, "Install", "mapFont 诊断 Hook 安装未成功", fields: new Dictionary<string, object?> { ["target"] = target.Name, ["rva"] = $"0x{rva:X}" });
    }

    internal static void Uninstall()
    {
        if (IsInstalled)
        {
            CounterSnapshot counters = GetCountersSnapshot();
            DiagnosticLogger.Start(
                Tag,
                "Uninstall",
                "开始卸载 mapFont 诊断 Hook",
                new Dictionary<string, object?>
                {
                    ["hitCount"] = counters.HitCount,
                    ["redirects"] = counters.RedirectCount,
                    ["earlyRegisters"] = counters.EarlyRegisterCount,
                    ["styleScopeHits"] = counters.StyleScopeHitCount,
                    ["mTextScopeHits"] = counters.MTextScopeHitCount,
                    ["sampleOverflow"] = counters.SampleOverflowCount
                });
        }
        else
        {
            DiagnosticLogger.Skip(Tag, "Uninstall", "mapFont 诊断 Hook 未安装，跳过卸载");
        }

        _hook?.Uninstall();
        _hook = null;
        _hookDelegate = null;
        ResetDiagnostics();
        DiagnosticLogger.Ok(Tag, "Uninstall", "mapFont 诊断 Hook 卸载流程完成");
    }

    internal static void LogDocumentSummary(CounterSnapshot before)
    {
        if (!IsInstalled)
            return;

        CounterSnapshot after = GetCountersSnapshot();
        long hits = after.HitCount - before.HitCount;
        long redirects = after.RedirectCount - before.RedirectCount;
        long earlyRegisters = after.EarlyRegisterCount - before.EarlyRegisterCount;
        long styleHits = after.StyleScopeHitCount - before.StyleScopeHitCount;
        long mTextHits = after.MTextScopeHitCount - before.MTextScopeHitCount;
        long overflow = after.SampleOverflowCount - before.SampleOverflowCount;

        string sampleText = BuildSampleText(before.SampleSequence);
        DiagnosticLogger.Ok(
            Tag,
            "DocumentSummary",
            "本次文档 mapFont 计数已采集",
            new Dictionary<string, object?>
            {
                ["hits"] = hits,
                ["redirects"] = redirects,
                ["earlyRegisters"] = earlyRegisters,
                ["styleScopeHits"] = styleHits,
                ["mTextScopeHits"] = mTextHits,
                ["sampleOverflow"] = overflow,
                ["samples"] = sampleText
            });
    }

    private static int HookHandler(IntPtr name, IntPtr desc, IntPtr db)
    {
        var trampoline = _hook?.TrampolineDelegate;
        if (trampoline == null)
            return -1;

        if (_inHook)
        {
            return trampoline(name, desc, db);
        }

        _inHook = true;
        string requestName = FormatPointer(name);
        string descText = FormatPointer(desc);
        string dbText = FormatPointer(db);
        bool styleScope = false;
        bool mTextScope = false;
        try
        {
            EarlyRegistration? earlyRegistration = null;

            try
            {
                NativeStringValue nameValue = ReadNativeStringValue(name);
                requestName = nameValue.Display;

                long hitIndex = Interlocked.Increment(ref _hitCount);
                if (styleScope)
                    Interlocked.Increment(ref _styleScopeHitCount);
                if (mTextScope)
                    Interlocked.Increment(ref _mTextScopeHitCount);
                if (hitIndex <= FirstHitLogLimit)
                {
                    DiagnosticLogger.Ok(
                        Tag,
                        "HookHandler",
                        "mapFont 入站命中",
                        new Dictionary<string, object?>
                        {
                            ["hitIndex"] = hitIndex,
                            ["requestName"] = requestName,
                            ["desc"] = descText,
                            ["db"] = dbText,
                            ["styleScope"] = styleScope,
                            ["mTextScope"] = mTextScope
                        });
                }

                if (TryRegisterEarlyRuntimeRequest(nameValue, db, out earlyRegistration))
                {
                    long registerIndex = Interlocked.Increment(ref _earlyRegisterCount);
                    string logKey = string.Concat(
                        db.ToInt64().ToString("X"),
                        "|",
                        earlyRegistration!.Kind,
                        "|",
                        earlyRegistration.OriginalFont,
                        "|",
                        earlyRegistration.ReplacementFont);
                    if (registerIndex <= FirstEarlyRegisterLogLimit || EarlyRegisterLogSeen.TryAdd(logKey, 0))
                    {
                        DiagnosticLogger.Ok(
                            Tag,
                            "EarlyRegister",
                            "mapFont 早期登记运行时字体请求",
                            new Dictionary<string, object?>
                            {
                                ["registerIndex"] = registerIndex,
                                ["source"] = "MapFontEarlyRegister",
                                ["kind"] = earlyRegistration.Kind.ToString(),
                                ["original"] = earlyRegistration.OriginalFont,
                                ["replacement"] = earlyRegistration.ReplacementFont,
                                ["reason"] = earlyRegistration.Reason,
                                ["dbScope"] = dbText
                            });
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Fail(Tag, "HookHandlerPreSample", "mapFont 诊断采样前置异常", ex);
            }

            int result = trampoline(name, desc, db);
            try
            {
                RecordSample(
                    requestName,
                    descText,
                    dbText,
                    styleScope,
                    mTextScope,
                    false,
                    string.Empty,
                    result);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Fail(Tag, "HookHandlerPostSample", "mapFont 诊断采样后置异常", ex);
            }

            return result;
        }
        finally
        {
            _inHook = false;
        }
    }

    private static void RecordSample(
        string name,
        string desc,
        string db,
        bool styleScope,
        bool mTextScope,
        bool redirected,
        string redirectReplacement,
        int result)
    {
        long sequence = Interlocked.Increment(ref _sampleSequence);
        string key = string.Join(
            "\u001F",
            name,
            desc,
            db,
            styleScope,
            mTextScope,
            redirected,
            redirectReplacement);

        var incoming = new SampleRecord(
            name,
            desc,
            db,
            styleScope,
            mTextScope,
            redirected,
            redirectReplacement,
            sequence,
            sequence,
            1,
            result);

        if (Samples.ContainsKey(key) || Samples.Count < MaxSampleRecords)
        {
            Samples.AddOrUpdate(
                key,
                incoming,
                (_, existing) => existing with
                {
                    LastSequence = sequence,
                    Count = existing.Count + 1,
                    LastResult = result
                });
            return;
        }

        Interlocked.Increment(ref _sampleOverflowCount);
    }

    private static string BuildSampleText(long afterSequence)
    {
        var records = Samples.Values
            .Where(item => item.LastSequence > afterSequence)
            .OrderBy(item => item.FirstSequence)
            .Take(MaxDocumentSamples)
            .Select(FormatSample)
            .ToArray();

        return records.Length == 0 ? "none" : string.Join("; ", records);
    }

    private static string FormatSample(SampleRecord sample)
        => $"name='{sample.Name}' desc={sample.Desc} db={sample.Db} " +
           $"style={sample.StyleScope} mtext={sample.MTextScope} " +
           $"redirect={sample.Redirected}->{sample.RedirectReplacement} " +
           $"result={sample.LastResult} count={sample.Count}";

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

    private static void ResetDiagnostics()
    {
        Samples.Clear();
        EarlyRegisterLogSeen.Clear();
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _redirectCount, 0);
        Interlocked.Exchange(ref _earlyRegisterCount, 0);
        Interlocked.Exchange(ref _styleScopeHitCount, 0);
        Interlocked.Exchange(ref _mTextScopeHitCount, 0);
        Interlocked.Exchange(ref _sampleSequence, 0);
        Interlocked.Exchange(ref _sampleOverflowCount, 0);
    }

    private static NativeStringValue ReadNativeStringValue(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return new NativeStringValue(string.Empty, "<null>");

        string pointer = FormatPointer(value);
        if (!TryReadUtf16String(value, MaxNameChars, out string text))
            return new NativeStringValue(string.Empty, $"ptr={pointer}");

        return new NativeStringValue(text, $"{text}@{pointer}");
    }

    private static bool TryRegisterEarlyRuntimeRequest(
        NativeStringValue value,
        IntPtr dbScope,
        out EarlyRegistration? registration)
    {
        registration = null;

        string original = FontRedirectResolver.NormalizeInputName(value.Text);
        if (!FontRedirectResolver.HasAtPrefix(original))
            return false;

        if (dbScope == IntPtr.Zero)
        {
            LogEarlyRegisterSkip(original, dbScope, "缺少 db scope");
            return false;
        }

        string lookup = FontRedirectResolver.StripLeadingAtPrefix(original);
        if (string.IsNullOrWhiteSpace(lookup))
        {
            LogEarlyRegisterSkip(original, dbScope, "空基础字体名");
            return false;
        }

        if (TryRegisterEarlyShxRequest(original, lookup, dbScope, out registration))
            return true;

        if (TryRegisterEarlyTrueTypeRequest(original, lookup, dbScope, out registration))
            return true;

        LogEarlyRegisterSkip(original, dbScope, "未找到可用基础字体或类型不匹配");
        return false;
    }

    private static bool TryRegisterEarlyShxRequest(
        string original,
        string lookup,
        IntPtr dbScope,
        out EarlyRegistration? registration)
    {
        registration = null;
        if (FontRedirectResolver.IsTrueTypeFontFileName(lookup))
            return false;

        bool hasExtension = lookup.IndexOf('.') >= 0;
        if (hasExtension && !lookup.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            return false;

        string shxName = FontRedirectResolver.EnsureShx(lookup);
        if (!HookShxFontIndex.TryGetKind(shxName, out bool isBigFont))
            return false;

        var kind = isBigFont ? FontRedirectKind.ShxBigFont : FontRedirectKind.ShxMain;
        if (!FontRuntimeRequestRegistry.TryRegisterResolvedRequest(
                original,
                kind,
                "MapFontEarlyRegister",
                "mapFont",
                null,
                original,
                dbScope,
                out _,
                out string replacement))
        {
            LogEarlyRegisterSkip(original, dbScope, "SHX registry rejected");
            return false;
        }

        registration = new EarlyRegistration(original, replacement, kind, "基础SHX可用");
        return true;
    }

    private static bool TryRegisterEarlyTrueTypeRequest(
        string original,
        string lookup,
        IntPtr dbScope,
        out EarlyRegistration? registration)
    {
        registration = null;
        if (!FontRedirectResolver.IsAvailableTrueType(lookup))
            return false;

        if (!FontRuntimeRequestRegistry.TryRegisterResolvedRequest(
                original,
                FontRedirectKind.TrueType,
                "MapFontEarlyRegister",
                "mapFont",
                null,
                original,
                dbScope,
                out _,
                out string replacement))
        {
            LogEarlyRegisterSkip(original, dbScope, "TrueType registry rejected");
            return false;
        }

        registration = new EarlyRegistration(original, replacement, FontRedirectKind.TrueType, "基础TrueType可用");
        return true;
    }

    private static void LogEarlyRegisterSkip(string original, IntPtr dbScope, string reason)
    {
        string logKey = string.Concat("skip|", dbScope.ToInt64().ToString("X"), "|", original, "|", reason);
        if (!EarlyRegisterLogSeen.TryAdd(logKey, 0))
            return;

        DiagnosticLogger.Skip(
            Tag,
            "EarlyRegister",
            "mapFont 早期登记跳过",
            new Dictionary<string, object?>
            {
                ["source"] = "MapFontEarlyRegister",
                ["original"] = original,
                ["dbScope"] = FormatPointer(dbScope),
                ["reason"] = reason
            });
    }

    private static bool TryReadUtf16String(IntPtr value, int maxChars, out string text)
    {
        text = string.Empty;
        byte[] buffer = new byte[(maxChars + 1) * 2];
        if (!ReadProcessMemory(
                GetCurrentProcess(),
                value,
                buffer,
                (UIntPtr)buffer.Length,
                out UIntPtr bytesRead)
            || bytesRead == UIntPtr.Zero)
        {
            return false;
        }

        ulong bytesReadValue = bytesRead.ToUInt64();
        int byteCount = bytesReadValue > (ulong)buffer.Length ? buffer.Length : (int)bytesReadValue;
        int charCount = Math.Min(byteCount / 2, maxChars);
        var chars = new char[charCount];
        int length = 0;

        for (int index = 0; index < charCount; index++)
        {
            int byteIndex = index * 2;
            char current = (char)(buffer[byteIndex] | (buffer[byteIndex + 1] << 8));
            if (current == '\0')
                break;

            chars[length++] = char.IsControl(current) ? ' ' : current;
        }

        text = length == 0 ? string.Empty : new string(chars, 0, length);
        return true;
    }

    private static string FormatPointer(IntPtr value)
        => value == IntPtr.Zero ? "0x0" : $"0x{value.ToInt64():X}";

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        UIntPtr nSize,
        out UIntPtr lpNumberOfBytesRead);
}
#endif
