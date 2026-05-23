using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// Handles AutoCAD shpload TrueType runtime remapping while keeping ldfile focused on SHX loading.
/// </summary>
internal static class ShpLoadHook
{
    private const string Tag = "ShpLoadHook";
    private const int MaxSampleRecords = 96;
    private const int MaxDocumentSamples = 16;
    private const int MaxFileNameChars = 260;
    private const long FirstHitLogLimit = 8;
    private const long FirstRedirectLogLimit = 16;

    private static NativeInlineHook<ShpLoadDelegate>? _hook;
    private static ShpLoadDelegate? _hookDelegate;
    private static readonly ConcurrentDictionary<string, SampleRecord> Samples =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IntPtr> NativeStringCache =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> RedirectLogSeen =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _hitCount;
    private static long _redirectCount;
    private static long _styleScopeHitCount;
    private static long _mTextScopeHitCount;
    private static long _sampleSequence;
    private static long _sampleOverflowCount;

    [ThreadStatic] private static bool _inHook;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ShpLoadDelegate(
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
        bool StyleScope,
        bool MTextScope,
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
        string NormalizedRequest,
        string OriginalDisplayFont,
        string ReplacementFont,
        string Source,
        InlineFontType? InlineType,
        FontRuntimeRequest? Request);

    internal static bool IsInstalled => _hook?.IsInstalled == true;

    internal static CounterSnapshot GetCountersSnapshot()
        => new(
            Interlocked.Read(ref _hitCount),
            Interlocked.Read(ref _redirectCount),
            Interlocked.Read(ref _styleScopeHitCount),
            Interlocked.Read(ref _mTextScopeHitCount),
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
        _hookDelegate = HookHandler;
        _hook = new NativeInlineHook<ShpLoadDelegate>(Tag, target.Name, target.Rva ?? rva);
        _hook.InstallAtAddress(
            address,
            rva,
            _hookDelegate,
            target.MinPrologueSize,
            target.MaxPrologueSize,
            target.ExpectedPrefix);
        if (IsInstalled)
        {
            DiagnosticLogger.Ok(
                Tag,
                "Install",
                "shpload TrueType Hook 安装成功",
                new Dictionary<string, object?>
                {
                    ["target"] = target.Name,
                    ["rva"] = $"0x{rva:X}"
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
                    ["rva"] = $"0x{rva:X}"
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
                    ["styleScopeHits"] = counters.StyleScopeHitCount,
                    ["mTextScopeHits"] = counters.MTextScopeHitCount,
                    ["sampleOverflow"] = counters.SampleOverflowCount
                });
        }
        else
        {
            DiagnosticLogger.Skip(Tag, "Uninstall", "shpload Hook 未安装，跳过卸载");
        }

        _hook?.Uninstall();
        _hook = null;
        _hookDelegate = null;
        ResetDiagnostics();
        DiagnosticLogger.Ok(Tag, "Uninstall", "shpload TrueType Hook 卸载流程完成");
    }

    internal static void LogDocumentSummary(CounterSnapshot before)
    {
        if (!IsInstalled)
            return;

        CounterSnapshot after = GetCountersSnapshot();
        long hits = after.HitCount - before.HitCount;
        long redirects = after.RedirectCount - before.RedirectCount;
        long styleHits = after.StyleScopeHitCount - before.StyleScopeHitCount;
        long mTextHits = after.MTextScopeHitCount - before.MTextScopeHitCount;
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
                ["styleScopeHits"] = styleHits,
                ["mTextScopeHits"] = mTextHits,
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
        var trampoline = _hook?.TrampolineDelegate;
        if (trampoline == null)
            return -1;

        if (_inHook)
        {
            return trampoline(fileName, param2, db, flag, arg5, arg6, arg7, arg8, charset, pitch, family);
        }

        if (!FontRuntimeRequestRegistry.HasTrueTypeRequests)
            return trampoline(fileName, param2, db, flag, arg5, arg6, arg7, arg8, charset, pitch, family);

        _inHook = true;
        string requestName = FormatPointer(fileName);
        string arg5Text = FormatPointer(arg5);
        string arg6Text = FormatPointer(arg6);
        ShpLoadRedirectApplication? redirect = null;
        bool styleScope = false;
        bool mTextScope = false;
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
                styleScope = StyleTextStyleHook.IsInsideStyleRuntimeOperation;
                mTextScope = MTextInlineFontHook.IsInsideInlineFontHook;

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
                            ["styleScope"] = styleScope,
                            ["mTextScope"] = mTextScope
                        });
                }

                if (TryCreateRegisteredRedirect(arg6Value, "arg6", out redirect))
                {
                    effectiveArg6 = GetNativeString(redirect!.ReplacementFont);
                }
                else if (TryCreateRegisteredRedirect(fileNameValue, "fileName", out redirect))
                {
                    effectiveFileName = GetNativeString(redirect!.ReplacementFont);
                }
                else if (TryCreateRegisteredRedirect(arg5Value, "arg5", out redirect))
                {
                    effectiveArg5 = GetNativeString(redirect!.ReplacementFont);
                }

                if (redirect != null)
                {
                    long redirectIndex = Interlocked.Increment(ref _redirectCount);
                    string logKey = string.Concat(
                        redirect.ArgumentName,
                        "|",
                        redirect.NormalizedRequest,
                        "|",
                        redirect.ReplacementFont,
                        "|",
                        redirect.Source);
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
                                ["source"] = redirect.Source,
                                ["kind"] = "TrueType",
                                ["originalDisplayFont"] = redirect.OriginalDisplayFont,
                                ["replacement"] = redirect.ReplacementFont,
                                ["request"] = redirect.NormalizedRequest,
                                ["param2"] = param2,
                                ["styleScope"] = styleScope,
                                ["mTextScope"] = mTextScope
                            });
                    }

                    FontRuntimeRequestRegistry.MarkHit(redirect.NormalizedRequest, FontRedirectKind.TrueType);
                    if (redirect.Request != null)
                        FontRuntimeMappingStore.RecordRuntimeMapping(redirect.Request, "ShpLoadHook", "已映射");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Fail(Tag, "HookHandler.Preprocess", "shpload TrueType 处理前置异常", ex);
            }

            int result = trampoline(effectiveFileName, param2, db, flag, effectiveArg5, effectiveArg6, arg7, arg8, charset, pitch, family);
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
                    styleScope,
                    mTextScope,
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
        bool styleScope,
        bool mTextScope,
        bool redirected,
        string redirectArgument,
        string redirectReplacement,
        int result)
    {
        long sequence = Interlocked.Increment(ref _sampleSequence);
        string key = string.Join(
            "\u001F",
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
            styleScope,
            mTextScope,
            redirected,
            redirectArgument,
            redirectReplacement);

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
            styleScope,
            mTextScope,
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
        => $"file='{sample.FileName}' param2={sample.Param2} style={sample.StyleScope} mtext={sample.MTextScope} " +
           $"flag={sample.Flag} arg5='{sample.Arg5}' arg6='{sample.Arg6}' args={sample.Arg7}/{sample.Arg8} " +
           $"charset={sample.Charset} pitch={sample.Pitch} family={sample.Family} " +
           $"redirect={sample.Redirected}:{sample.RedirectArgument}->{sample.RedirectReplacement} " +
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
        foreach (IntPtr ptr in NativeStringCache.Values)
        {
            try { Marshal.FreeHGlobal(ptr); } catch { }
        }

        NativeStringCache.Clear();
        RedirectLogSeen.Clear();
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _redirectCount, 0);
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
        if (!TryReadUtf16String(value, MaxFileNameChars, out string text))
            return new NativeStringValue(string.Empty, $"ptr={pointer}");

        return new NativeStringValue(text, $"{text}@{pointer}");
    }

    private static bool TryCreateRegisteredRedirect(
        NativeStringValue value,
        string argumentName,
        out ShpLoadRedirectApplication? redirect)
    {
        redirect = null;
        if (string.IsNullOrWhiteSpace(value.Text))
            return false;

        if (FontRuntimeRequestRegistry.TryGetTrueTypeRequest(
                value.Text,
                out FontRuntimeRequest? match,
                out string normalized)
            && match != null)
        {
            if (string.Equals(normalized, match.ReplacementFont, StringComparison.OrdinalIgnoreCase))
                return false;

            redirect = new ShpLoadRedirectApplication(
                argumentName,
                normalized,
                match.OriginalDisplayFont,
                match.ReplacementFont,
                match.Source,
                match.InlineType,
                match);
            return true;
        }

        return false;
    }

    private static bool IsShxFontName(string fontName)
        => fontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);

    private static IntPtr GetNativeString(string value)
        => NativeStringCache.GetOrAdd(value, static text => Marshal.StringToHGlobalUni(text));

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
