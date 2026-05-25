using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 处理 shpload 阶段的 TrueType / @TrueType 文件级重定向。
/// </summary>
internal static class ShpLoadHook
{
    private const string Tag = "ShpLoadHook";
    private const int MaxSampleRecords = 96;
    private const int MaxDocumentSamples = 16;
    private const int MaxFileNameChars = 260;
    private const long FirstHitLogLimit = 8;
    private const long FirstRedirectLogLimit = 16;
    private const int TrueTypeStrictBypassLogLimit = 24;
    private const int FontTypeRegular = 0;
    private const int FontTypeBigFont = 4;
    private const string LegacyAbiName = "_N00HH int/int";
    private const string Abi2027Name = "_N0022 bool/bool";

    private static NativeInlineHook<ShpLoadLegacyDelegate>? _legacyHook;
    private static ShpLoadLegacyDelegate? _legacyHookDelegate;
    private static NativeInlineHook<ShpLoad2027Delegate>? _hook2027;
    private static ShpLoad2027Delegate? _hook2027Delegate;
    private static readonly ConcurrentDictionary<string, SampleRecord> Samples =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IntPtr> NativeStringCache =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> RedirectLogSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> TrueTypeStrictBypassLogSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _hitCount;
    private static long _redirectCount;
    private static long _sampleSequence;
    private static long _sampleOverflowCount;
    private static long _strictBypassLogCount;
    // 缓存伪句柄，避免每次 Hook 命中都调用 GetCurrentProcess。
    private static readonly IntPtr s_currentProcess = GetCurrentProcess();

    [ThreadStatic] private static bool _inHook;
    // Hook 回调受 _inHook 防重入保护，线程本地缓冲区可安全复用。
    [ThreadStatic] private static byte[]? _readBuffer;
    [ThreadStatic] private static char[]? _charBuffer;
    // 复用采样 key 缓冲区，降低 Hook 热路径分配。
    [ThreadStatic] private static System.Text.StringBuilder? _sampleKeyBuilder;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ShpLoadLegacyDelegate(
        IntPtr fileName,
        int param2,
        IntPtr db,
        byte flag,
        IntPtr arg5,
        IntPtr arg6,
        int arg7,
        int arg8,
        int charset,
        int pitch,
        int family);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ShpLoad2027Delegate(
        IntPtr fileName,
        int param2,
        IntPtr db,
        byte flag,
        IntPtr arg5,
        IntPtr arg6,
        byte arg7,
        byte arg8,
        int charset,
        int pitch,
        int family);

    private delegate int ShpLoadTrampolineCall(
        IntPtr effectiveFileName,
        IntPtr effectiveArg5,
        IntPtr effectiveArg6);

    internal sealed record CounterSnapshot(
        long HitCount,
        long RedirectCount,
        long StyleScopeHitCount,
        long MTextScopeHitCount,
        long SampleSequence,
        long SampleOverflowCount);

    private sealed record SampleRecord(
        string FileName,
        int Param2,
        byte Flag,
        string Arg5,
        string Arg6,
        int Arg7,
        int Arg8,
        int Charset,
        int Pitch,
        int Family,
        string Abi,
        bool Redirected,
        string RedirectArgument,
        string RedirectReplacement,
        long FirstSequence,
        long LastSequence,
        long Count,
        int LastResult);

    private readonly record struct NativeStringValue(string Text, string Display);

    private sealed record ShpLoadRedirectApplication(
        string ArgumentName,
        string OriginalFont,
        string ReplacementFont,
        string Reason);

    internal static bool IsInstalled => _legacyHook?.IsInstalled == true || _hook2027?.IsInstalled == true;

    internal static CounterSnapshot GetCountersSnapshot()
        => new(
            Interlocked.Read(ref _hitCount),
            Interlocked.Read(ref _redirectCount),
            0,
            0,
            Interlocked.Read(ref _sampleSequence),
            Interlocked.Read(ref _sampleOverflowCount));

    internal static void Install()
    {
        if (IsInstalled)
        {
            DiagnosticLogger.Skip(Tag, "Install", "shpload Hook 已安装，跳过重复安装");
            return;
        }

        DiagnosticLogger.Start(Tag, "Install", "开始安装 shpload TrueType Hook");

        if (PlatformManager.Platform is not INativeFontHookExportsProvider exports)
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "当前平台未提供 native Hook profile，跳过 shpload Hook",
                new Dictionary<string, object?> { ["platform"] = PlatformManager.Platform.DisplayName });
            return;
        }

        IntPtr module = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "AcDb 模块未加载，跳过 shpload Hook",
                new Dictionary<string, object?> { ["module"] = PlatformManager.Platform.AcDbDllName });
            return;
        }

        NativeHookTarget target = exports.NativeFontHookProfile.ShpLoad;
        if (!TryGetExportAddress(module, target, out IntPtr address, out uint rva))
        {
            DiagnosticLogger.Skip(
                Tag,
                "Install",
                "shpload 入口未通过强校验，跳过 TrueType Hook",
                new Dictionary<string, object?> { ["target"] = target.Name });
            return;
        }

        ResetDiagnostics();
        string abi = GetCurrentAbiName();
        if (Uses2027Abi())
        {
            Install2027Hook(address, rva, target);
        }
        else
        {
            InstallLegacyHook(address, rva, target);
        }

        if (IsInstalled)
        {
            DiagnosticLogger.Ok(
                Tag,
                "Install",
                "shpload TrueType Hook 安装成功",
                new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["rva"] = $"0x{rva:X}",
                    ["abi"] = abi
                });
        }
        else
        {
            DiagnosticLogger.Fail(
                Tag,
                "Install",
                "shpload TrueType Hook 安装未成功",
                fields: new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["rva"] = $"0x{rva:X}",
                    ["abi"] = abi
                });
        }
    }

    internal static void Uninstall()
    {
        if (IsInstalled)
        {
            CounterSnapshot counters = GetCountersSnapshot();
            DiagnosticLogger.Start(
                Tag,
                "Uninstall",
                "开始卸载 shpload TrueType Hook",
                new Dictionary<string, object?>
                {
                    ["hitCount"] = counters.HitCount,
                    ["redirects"] = counters.RedirectCount,
                    ["sampleOverflow"] = counters.SampleOverflowCount
                });
        }
        else
        {
            DiagnosticLogger.Skip(Tag, "Uninstall", "shpload Hook 未安装，跳过卸载");
        }

        _legacyHook?.Uninstall();
        _legacyHook = null;
        _legacyHookDelegate = null;
        _hook2027?.Uninstall();
        _hook2027 = null;
        _hook2027Delegate = null;
        ResetDiagnostics();
        DiagnosticLogger.Ok(Tag, "Uninstall", "shpload TrueType Hook 卸载流程完成");
    }

    private static void InstallLegacyHook(IntPtr address, uint rva, NativeHookTarget target)
    {
        _legacyHookDelegate = HookHandler;
        _legacyHook = new NativeInlineHook<ShpLoadLegacyDelegate>(Tag, target.Name, target.Rva ?? rva);
        _legacyHook.InstallAtAddress(
            address,
            rva,
            _legacyHookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    private static void Install2027Hook(IntPtr address, uint rva, NativeHookTarget target)
    {
        _hook2027Delegate = HookHandler2027;
        _hook2027 = new NativeInlineHook<ShpLoad2027Delegate>(Tag, target.Name, target.Rva ?? rva);
        _hook2027.InstallAtAddress(
            address,
            rva,
            _hook2027Delegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
    }

    internal static void LogDocumentSummary(CounterSnapshot before)
    {
        if (!IsInstalled)
            return;

        CounterSnapshot after = GetCountersSnapshot();
        long hits = after.HitCount - before.HitCount;
        long redirects = after.RedirectCount - before.RedirectCount;
        long overflow = after.SampleOverflowCount - before.SampleOverflowCount;

        string sampleText = BuildSampleText(before.SampleSequence);
        DiagnosticLogger.Ok(
            Tag,
            "DocumentSummary",
            "本次文档 shpload 计数已采集",
            new Dictionary<string, object?>
            {
                ["hits"] = hits,
                ["redirects"] = redirects,
                ["sampleOverflow"] = overflow,
                ["samples"] = sampleText
            });
    }

    private static int HookHandler(
        IntPtr fileName,
        int param2,
        IntPtr db,
        byte flag,
        IntPtr arg5,
        IntPtr arg6,
        int arg7,
        int arg8,
        int charset,
        int pitch,
        int family)
    {
        var trampoline = _legacyHook?.TrampolineDelegate;
        if (trampoline == null)
            return -1;

        return HandleHook(
            fileName,
            param2,
            db,
            flag,
            arg5,
            arg6,
            arg7,
            arg8,
            charset,
            pitch,
            family,
            LegacyAbiName,
            (effectiveFileName, effectiveArg5, effectiveArg6) => trampoline(
                effectiveFileName,
                param2,
                db,
                flag,
                effectiveArg5,
                effectiveArg6,
                arg7,
                arg8,
                charset,
                pitch,
                family));
    }

    private static int HookHandler2027(
        IntPtr fileName,
        int param2,
        IntPtr db,
        byte flag,
        IntPtr arg5,
        IntPtr arg6,
        byte arg7,
        byte arg8,
        int charset,
        int pitch,
        int family)
    {
        var trampoline = _hook2027?.TrampolineDelegate;
        if (trampoline == null)
            return -1;

        return HandleHook(
            fileName,
            param2,
            db,
            flag,
            arg5,
            arg6,
            arg7,
            arg8,
            charset,
            pitch,
            family,
            Abi2027Name,
            (effectiveFileName, effectiveArg5, effectiveArg6) => trampoline(
                effectiveFileName,
                param2,
                db,
                flag,
                effectiveArg5,
                effectiveArg6,
                arg7,
                arg8,
                charset,
                pitch,
                family));
    }

    private static int HandleHook(
        IntPtr fileName,
        int param2,
        IntPtr db,
        byte flag,
        IntPtr arg5,
        IntPtr arg6,
        int arg7,
        int arg8,
        int charset,
        int pitch,
        int family,
        string abi,
        ShpLoadTrampolineCall trampoline)
    {
        if (_inHook)
            return trampoline(fileName, arg5, arg6);

        _inHook = true;
        string requestName = FormatPointer(fileName);
        string arg5Text = FormatPointer(arg5);
        string arg6Text = FormatPointer(arg6);
        ShpLoadRedirectApplication? redirect = null;
        try
        {
            IntPtr effectiveFileName = fileName;
            IntPtr effectiveArg5 = arg5;
            IntPtr effectiveArg6 = arg6;

            try
            {
                NativeStringValue fileNameValue = ReadNativeStringValue(fileName);
                NativeStringValue arg5Value = ReadNativeStringValue(arg5);
                NativeStringValue arg6Value = ReadNativeStringValue(arg6);
                requestName = fileNameValue.Display;
                arg5Text = arg5Value.Display;
                arg6Text = arg6Value.Display;

                long hitIndex = Interlocked.Increment(ref _hitCount);
                if (hitIndex <= FirstHitLogLimit)
                {
                    DiagnosticLogger.Ok(
                        Tag,
                        "HookHandler",
                        "shpload 入站命中",
                        new Dictionary<string, object?>
                        {
                            ["hitIndex"] = hitIndex,
                            ["fileName"] = requestName,
                            ["param2"] = param2,
                            ["db"] = FormatPointer(db),
                            ["flag"] = flag,
                            ["arg5"] = arg5Text,
                            ["arg6"] = arg6Text,
                            ["arg7"] = arg7,
                            ["arg8"] = arg8,
                            ["charset"] = charset,
                            ["pitch"] = pitch,
                            ["family"] = family,
                            ["abi"] = abi
                        });
                }

                if (TryCreateDirectRedirect(arg6Value, "arg6", param2, out redirect))
                {
                    effectiveArg6 = GetNativeString(redirect!.ReplacementFont);
                }
                else if (TryCreateDirectRedirect(fileNameValue, "fileName", param2, out redirect))
                {
                    effectiveFileName = GetNativeString(redirect!.ReplacementFont);
                }
                else if (TryCreateDirectRedirect(arg5Value, "arg5", param2, out redirect))
                {
                    effectiveArg5 = GetNativeString(redirect!.ReplacementFont);
                }

                if (redirect != null)
                {
                    long redirectIndex = Interlocked.Increment(ref _redirectCount);
                    string logKey = string.Concat(
                        redirect.ArgumentName,
                        "|",
                        redirect.OriginalFont,
                        "|",
                        redirect.ReplacementFont);
                    if (redirectIndex <= FirstRedirectLogLimit || RedirectLogSeen.TryAdd(logKey, 0))
                    {
                        DiagnosticLogger.Ok(
                            Tag,
                            "HookHandler",
                            "shpload TrueType 重定向",
                            new Dictionary<string, object?>
                            {
                                ["redirectIndex"] = redirectIndex,
                                ["argument"] = redirect.ArgumentName,
                                ["kind"] = "TrueType",
                                ["original"] = redirect.OriginalFont,
                                ["replacement"] = redirect.ReplacementFont,
                                ["param2"] = param2,
                                ["reason"] = redirect.Reason
                            });
                    }

                    FontRuntimeMappingStore.RecordRuntimeMapping(
                        "文件级",
                        string.Empty,
                        redirect.OriginalFont,
                        GetBaseFont(redirect.OriginalFont),
                        "TrueType字体",
                        redirect.ReplacementFont,
                        "ShpLoadHook",
                        "已映射");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Fail(Tag, "HookHandler.Preprocess", "shpload TrueType 处理前置异常", ex);
            }

            int result = trampoline(effectiveFileName, effectiveArg5, effectiveArg6);
            try
            {
                RecordSample(
                    requestName,
                    param2,
                    flag,
                    arg5Text,
                    arg6Text,
                    arg7,
                    arg8,
                    charset,
                    pitch,
                    family,
                    abi,
                    redirect != null,
                    redirect?.ArgumentName ?? string.Empty,
                    redirect?.ReplacementFont ?? string.Empty,
                    result);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Fail(Tag, "HookHandler.PostSample", "shpload TrueType 采样后置异常", ex);
            }

            return result;
        }
        finally
        {
            _inHook = false;
        }
    }

    private static void RecordSample(
        string fileName,
        int param2,
        byte flag,
        string arg5,
        string arg6,
        int arg7,
        int arg8,
        int charset,
        int pitch,
        int family,
        string abi,
        bool redirected,
        string redirectArgument,
        string redirectReplacement,
        int result)
    {
        long sequence = Interlocked.Increment(ref _sampleSequence);
        // 手动拼 key，避免 string.Join 带来的装箱和 object[] 分配。
        const char Sep = '\u001F';
        var sb = _sampleKeyBuilder ??= new System.Text.StringBuilder(512);
        sb.Clear();
        sb.Append(fileName).Append(Sep).Append(param2).Append(Sep).Append(flag)
          .Append(Sep).Append(arg5).Append(Sep).Append(arg6).Append(Sep).Append(arg7)
          .Append(Sep).Append(arg8).Append(Sep).Append(charset).Append(Sep).Append(pitch)
          .Append(Sep).Append(family).Append(Sep).Append(abi)
          .Append(Sep).Append(redirected ? '1' : '0')
          .Append(Sep).Append(redirectArgument).Append(Sep).Append(redirectReplacement);
        string key = sb.ToString();

        var incoming = new SampleRecord(
            fileName,
            param2,
            flag,
            arg5,
            arg6,
            arg7,
            arg8,
            charset,
            pitch,
            family,
            abi,
            redirected,
            redirectArgument,
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
        => $"file='{sample.FileName}' param2={sample.Param2} " +
           $"flag={sample.Flag} arg5='{sample.Arg5}' arg6='{sample.Arg6}' args={sample.Arg7}/{sample.Arg8} " +
           $"charset={sample.Charset} pitch={sample.Pitch} family={sample.Family} " +
           $"abi={sample.Abi} " +
           $"redirect={sample.Redirected}:{sample.RedirectArgument}->{sample.RedirectReplacement} " +
           $"result={sample.LastResult} count={sample.Count}";

    private static bool Uses2027Abi()
        => string.Equals(PlatformManager.Platform.VersionName, "2027", StringComparison.Ordinal);

    private static string GetCurrentAbiName()
        => Uses2027Abi() ? Abi2027Name : LegacyAbiName;

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
        string? expectedRva = target.Rva.HasValue ? $"0x{target.Rva.Value:X}" : null;
        string actualRva = $"0x{rva:X}";
        bool rvaMatched = !target.Rva.HasValue || target.Rva.Value == rva;
        // RVA 只作为版本指纹输出；安装安全由导出名、入口字节和序言扫描共同决定。
        if (!rvaMatched)
        {
            DiagnosticLogger.Ok(
                Tag,
                "ResolveExport",
                "Hook 导出 RVA 与版本指纹不匹配，继续按导出地址安装",
                new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["expectedRva"] = expectedRva,
                    ["actualRva"] = actualRva,
                    ["rva"] = actualRva,
                    ["rvaMatched"] = false
                });
        }

        DiagnosticLogger.Ok(
            Tag,
            "ResolveExport",
            "Hook 导出解析成功",
            new Dictionary<string, object?>
            {
                ["target"] = target.Name,
                ["expectedRva"] = expectedRva,
                ["actualRva"] = actualRva,
                ["rva"] = actualRva,
                ["rvaMatched"] = rvaMatched
            });
        return true;
    }

    /// <summary>
    /// 文档开始时重置本轮 bypass 日志抑制状态。
    /// <para>
    /// 计数器不清零，<see cref="LogDocumentSummary"/> 需要用快照 delta 统计单文档命中。
    /// </para>
    /// </summary>
    internal static void ResetDocumentDiagnostics()
    {
        TrueTypeStrictBypassLogSeen.Clear();
        Interlocked.Exchange(ref _strictBypassLogCount, 0);
    }

    private static void ResetDiagnostics()
    {
        Samples.Clear();
        foreach (IntPtr ptr in NativeStringCache.Values)
        {
            try { Marshal.FreeHGlobal(ptr); } catch { }
        }

        NativeStringCache.Clear();
        RedirectLogSeen.Clear();
        TrueTypeStrictBypassLogSeen.Clear();
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _redirectCount, 0);
        Interlocked.Exchange(ref _sampleSequence, 0);
        Interlocked.Exchange(ref _sampleOverflowCount, 0);
        Interlocked.Exchange(ref _strictBypassLogCount, 0);
    }

    private static NativeStringValue ReadNativeStringValue(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return new NativeStringValue(string.Empty, "<null>");

        string pointer = FormatPointer(value);
        if (!TryReadUtf16String(value, MaxFileNameChars, out string text))
            return new NativeStringValue(string.Empty, $"ptr={pointer}");

        return new NativeStringValue(text, $"{text}@{pointer}");
    }

    private static bool TryCreateDirectRedirect(
        NativeStringValue value,
        string argumentName,
        int param2,
        out ShpLoadRedirectApplication? redirect)
    {
        redirect = null;

        string original = FontRedirectResolver.NormalizeInputName(value.Text);
        if (string.IsNullOrWhiteSpace(original))
            return false;

        if (!IsConfirmedTrueTypeRequest(original, argumentName, param2, out string bypassReason))
        {
            LogTrueTypeStrictBypass(original, argumentName, param2, bypassReason);
            return false;
        }

        if (IsTrueTypeLoadAvailable(original))
            return false;
        if (!TryGetConfiguredTrueTypeReplacement(original, out string replacement, out string redirectReason))
        {
            RecordTrueTypeMappingFailure(original, redirectReason);
            return false;
        }
        if (string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase))
        {
            RecordTrueTypeMappingFailure(original, "替换字体与缺失字体相同");
            return false;
        }

        redirect = new ShpLoadRedirectApplication(
            argumentName,
            original,
            replacement,
            redirectReason);
        return true;
    }

    private static bool IsConfirmedTrueTypeRequest(
        string original,
        string argumentName,
        int param2,
        out string bypassReason)
    {
        bypassReason = string.Empty;

        if (FontRedirectResolver.IsTrueTypeFontFileName(original))
            return true;

        if (IsShxLikeRequest(original))
        {
            bypassReason = "SHX 请求";
            return false;
        }

        if (HasNonTrueTypeExtension(original))
        {
            bypassReason = "非 TrueType 文件扩展名";
            return false;
        }

        if (IsFileLoadArgument(argumentName) && IsRegularOrBigFontParam(param2))
        {
            bypassReason = "fileName/arg5 的 param2=0/4 按 SHX 加载槽位放行";
            return false;
        }

        if (argumentName.Equals("arg6", StringComparison.Ordinal))
            return true;

        if (IsTrueTypeLoadAvailable(original))
            return true;

        bypassReason = "未确认 TrueType";
        return false;
    }

    private static bool IsShxLikeRequest(string original)
    {
        // original 已由调用方规范化。
        if (string.IsNullOrWhiteSpace(original))
            return false;

        if (original.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            return true;

        // 跳过 @ 后若已有扩展名，则不按 SHX 候选处理。
        int start = original.Length > 0 && original[0] == '@' ? 1 : 0;
        int lookupLen = original.Length - start;
        if (lookupLen == 0)
            return false;

#if NET8_0_OR_GREATER
        // .NET 8+ 直接在原串 span 上拼 .shx，减少中间字符串。
        if (Path.HasExtension(original.AsSpan(start)))
            return false;
        string shxName = string.Concat(original.AsSpan(start), ".shx".AsSpan());
#else
        // .NET Framework 无 span 路径，退回字符串切片。
        string lookup = start > 0 ? original[start..] : original;
        if (Path.HasExtension(lookup))
            return false;
        string shxName = lookup + ".shx";
#endif
        return ShxFontAvailabilityIndex.IsExactAvailable(shxName)
               || ShxFontAvailabilityIndex.IsAvailableWithAtFallback(shxName);
    }

    private static bool HasNonTrueTypeExtension(string original)
    {
        // StripLeadingAtPrefix 已含规范化，避免重复处理。
        string lookup = FontRedirectResolver.StripLeadingAtPrefix(original);
        return Path.HasExtension(lookup)
               && !FontRedirectResolver.IsTrueTypeFontFileName(lookup);
    }

    private static bool IsFileLoadArgument(string argumentName)
        => argumentName.Equals("fileName", StringComparison.Ordinal)
           || argumentName.Equals("arg5", StringComparison.Ordinal);

    private static bool IsRegularOrBigFontParam(int param2)
        => param2 == FontTypeRegular || param2 == FontTypeBigFont;

    private static void LogTrueTypeStrictBypass(
        string original,
        string argumentName,
        int param2,
        string reason)
    {
        if (Interlocked.Read(ref _strictBypassLogCount) >= TrueTypeStrictBypassLogLimit)
            return;

        string logKey = string.Concat(argumentName, "|", original, "|", param2.ToString(), "|", reason);
        if (!TrueTypeStrictBypassLogSeen.TryAdd(logKey, 0))
            return;

        if (Interlocked.Increment(ref _strictBypassLogCount) > TrueTypeStrictBypassLogLimit)
            return;

        DiagnosticLogger.Skip(
            Tag,
            "TrueTypeStrictBypass",
            "非确认 TrueType 请求已放行，ShpLoadHook 不处理",
            new Dictionary<string, object?>
            {
                ["argument"] = argumentName,
                ["original"] = original,
                ["param2"] = param2,
                ["reason"] = reason
            });
    }

    private static void RecordTrueTypeMappingFailure(string original, string reason)
    {
        FontRuntimeMappingStore.RecordFailedRuntimeMapping(
            "文件级",
            string.Empty,
            original,
            GetBaseFont(original),
            "TrueType字体",
            "ShpLoadHook",
            reason);
    }

    private static bool TryGetConfiguredTrueTypeReplacement(
        string original,
        out string replacement,
        out string reason)
    {
        replacement = string.Empty;
        reason = string.Empty;
        bool preserveAtPrefix = FontRedirectResolver.HasAtPrefix(original);
        if (preserveAtPrefix)
        {
            string configuredAtBase = FontRedirectResolver.StripLeadingAtPrefix(
                ConfigService.Instance.TrueTypeFont ?? string.Empty);
            if (!TrueTypeFontAvailabilityIndex.TryGetResolvedAtTrueTypeFont(
                    out string resolvedAtBaseFont,
                    out string source))
            {
                reason = "@TrueType 未找到可用的 @face 兜底字体";
                DiagnosticLogger.Skip(
                    Tag,
                    "ResolveAtTrueTypeReplacement",
                    "@TrueType 未找到可用的 @face 兜底字体，跳过 shpload 映射",
                    new Dictionary<string, object?>
                    {
                        ["original"] = original,
                        ["configuredTrueTypeFont"] = configuredAtBase
                    });
                return false;
            }

            replacement = "@" + resolvedAtBaseFont;
            reason = source.Equals("Configured", StringComparison.Ordinal)
                ? "配置 TrueType 兜底"
                : "配置 TrueType 不支持 @，使用系统兜底";
            return true;
        }

        if (!FontRedirectResolver.TryResolveConfiguredReplacement(FontRedirectKind.TrueType, out string configuredReplacement))
        {
            reason = "未找到可用 TrueType 兜底字体";
            return false;
        }

        string configured = FontRedirectResolver.StripLeadingAtPrefix(configuredReplacement);
        if (string.IsNullOrWhiteSpace(configured))
        {
            reason = "TrueType 兜底字体为空";
            return false;
        }

        replacement = configured;
        reason = "配置 TrueType 兜底";
        return true;
    }

    private static bool IsTrueTypeLoadAvailable(string fontName)
    {
        string normalized = FontRedirectResolver.NormalizeInputName(fontName);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return TrueTypeFontAvailabilityIndex.IsAvailable(normalized);
    }

    private static string GetBaseFont(string fontName)
    {
        string n = FontRedirectResolver.NormalizeInputName(fontName);
        return n.Length > 1 && n[0] == '@' ? n[1..] : string.Empty;
    }

    private static IntPtr GetNativeString(string value)
        => NativeStringCache.GetOrAdd(value, static text => Marshal.StringToHGlobalUni(text));

    private static bool TryReadUtf16String(IntPtr value, int maxChars, out string text)
    {
        text = string.Empty;
        int byteLen = (maxChars + 1) * 2;
        // 复用线程本地缓冲区，减少 Hook 热路径分配。
        if (_readBuffer == null || _readBuffer.Length < byteLen)
            _readBuffer = new byte[byteLen];
        byte[] buffer = _readBuffer;

        if (!ReadProcessMemory(
                s_currentProcess,
                value,
                buffer,
                (UIntPtr)byteLen,
                out UIntPtr bytesRead)
            || bytesRead == UIntPtr.Zero)
        {
            return false;
        }

        ulong bytesReadValue = bytesRead.ToUInt64();
        int byteCount = bytesReadValue > (ulong)byteLen ? byteLen : (int)bytesReadValue;
        int charCount = Math.Min(byteCount / 2, maxChars);

        if (_charBuffer == null || _charBuffer.Length < charCount)
            _charBuffer = new char[charCount];
        char[] chars = _charBuffer;
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

#if NET7_0_OR_GREATER
#pragma warning disable SYSLIB1054
#endif
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
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
#if NET7_0_OR_GREATER
#pragma warning restore SYSLIB1054
#endif
}
