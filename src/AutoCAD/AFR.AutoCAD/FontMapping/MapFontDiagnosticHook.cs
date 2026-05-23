#if DEBUG
using System.Collections.Concurrent;
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
    private const long FirstRedirectLogLimit = 16;

    private static NativeInlineHook<MapFontDelegate>? _hook;
    private static MapFontDelegate? _hookDelegate;
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
    private delegate int MapFontDelegate(IntPtr name, IntPtr desc, IntPtr db);

    internal sealed record CounterSnapshot(
        long HitCount,
        long RedirectCount,
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

    private sealed record DiagnosticRedirectApplication(
        string NormalizedRequest,
        string OriginalDisplayFont,
        string ReplacementFont,
        string Source,
        InlineFontType? InlineType);

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
            return;

        DiagnosticLogger.Log(Tag, "Debug 构建默认启用，开始安装 mapFont 诊断 Hook。");

        if (PlatformManager.Platform is not INativeFontHookExportsProvider exports)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.DisplayName} 未提供 native Hook profile，跳过 mapFont 诊断。");
            return;
        }

        IntPtr module = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过 mapFont 诊断。");
            return;
        }

        NativeHookTarget target = exports.NativeFontHookProfile.MapFont;
        if (!TryGetExportAddress(module, target, out IntPtr address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "mapFont 入口未通过强校验，跳过诊断 Hook。");
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
    }

    internal static void Uninstall()
    {
        if (IsInstalled)
        {
            CounterSnapshot counters = GetCountersSnapshot();
            DiagnosticLogger.Log(Tag,
                $"已卸载。HitCount={counters.HitCount}, Redirects={counters.RedirectCount}, " +
                $"StyleScopeHits={counters.StyleScopeHitCount}, " +
                $"MTextScopeHits={counters.MTextScopeHitCount}, SampleOverflow={counters.SampleOverflowCount}");
        }

        _hook?.Uninstall();
        _hook = null;
        _hookDelegate = null;
        ResetDiagnostics();
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
        DiagnosticLogger.Log(Tag,
            $"本次文档 mapFont 计数: hits={hits}, redirects={redirects}, styleScopeHits={styleHits}, " +
            $"mTextScopeHits={mTextHits}, sampleOverflow={overflow}, samples=[{sampleText}]");
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
        DiagnosticRedirectApplication? redirect = null;
        bool styleScope = false;
        bool mTextScope = false;
        try
        {
            IntPtr effectiveName = name;

            try
            {
                NativeStringValue nameValue = ReadNativeStringValue(name);
                requestName = nameValue.Display;
                styleScope = StyleTextStyleHook.IsInsideStyleRuntimeOperation;
                mTextScope = MTextInlineFontHook.IsInsideInlineFontHook;

                long hitIndex = Interlocked.Increment(ref _hitCount);
                if (styleScope)
                    Interlocked.Increment(ref _styleScopeHitCount);
                if (mTextScope)
                    Interlocked.Increment(ref _mTextScopeHitCount);
                if (hitIndex <= FirstHitLogLimit)
                {
                    DiagnosticLogger.Log(Tag,
                        $"mapFont 入站#{hitIndex}: name='{requestName}' desc={descText} db={dbText} " +
                        $"style={styleScope} mtext={mTextScope}");
                }

                if (TryCreateDiagnosticRedirect(nameValue, out redirect))
                {
                    effectiveName = GetNativeString(redirect!.ReplacementFont);
                    long redirectIndex = Interlocked.Increment(ref _redirectCount);
                    string logKey = string.Concat(
                        redirect.NormalizedRequest,
                        "|",
                        redirect.ReplacementFont,
                        "|",
                        redirect.Source);
                    if (redirectIndex <= FirstRedirectLogLimit || RedirectLogSeen.TryAdd(logKey, 0))
                    {
                        DiagnosticLogger.Log(Tag,
                            $"mapFont 诊断重定向#{redirectIndex}: source={redirect.Source} " +
                            $"kind=TrueType '{redirect.OriginalDisplayFont}' -> '{redirect.ReplacementFont}' " +
                            $"request='{redirect.NormalizedRequest}' style={styleScope} mtext={mTextScope}");
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(Tag + ": mapFont 诊断采样前置异常", ex);
            }

            int result = trampoline(effectiveName, desc, db);
            try
            {
                RecordSample(
                    requestName,
                    descText,
                    dbText,
                    styleScope,
                    mTextScope,
                    redirect != null,
                    redirect?.ReplacementFont ?? string.Empty,
                    result);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(Tag + ": mapFont 诊断采样后置异常", ex);
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
            DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：{target.DisabledReason ?? "缺少导出符号"}");
            return false;
        }

        address = NativeInlineHookInterop.GetProcAddress(module, target.ExportName!);
        if (address == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 导出未找到，跳过。");
            return false;
        }

        long delta = address.ToInt64() - module.ToInt64();
        if (delta <= 0 || delta > uint.MaxValue)
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} RVA 解析失败，跳过。Address=0x{address.ToInt64():X}");
            address = IntPtr.Zero;
            return false;
        }

        rva = (uint)delta;
        if (target.Rva.HasValue && target.Rva.Value != rva)
        {
            DiagnosticLogger.Log(Tag,
                $"{target.Name} RVA 不匹配，跳过。Expected=0x{target.Rva.Value:X}, Actual=0x{rva:X}");
            address = IntPtr.Zero;
            rva = 0;
            return false;
        }

        DiagnosticLogger.Log(Tag, $"{target.Name} 导出解析成功。RVA=0x{rva:X}");
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
        if (!TryReadUtf16String(value, MaxNameChars, out string text))
            return new NativeStringValue(string.Empty, $"ptr={pointer}");

        return new NativeStringValue(text, $"{text}@{pointer}");
    }

    private static bool TryCreateDiagnosticRedirect(
        NativeStringValue value,
        out DiagnosticRedirectApplication? redirect)
    {
        redirect = null;
        if (string.IsNullOrWhiteSpace(value.Text))
            return false;

        if (!LdFileHook.TryGetRegisteredTrueTypeRedirect(
                value.Text,
                out LdFileHook.RuntimeBridgeRedirect? match)
            || match == null)
        {
            return false;
        }

        redirect = new DiagnosticRedirectApplication(
            match.NormalizedRequest,
            match.OriginalDisplayFont,
            match.ReplacementFont,
            match.Source,
            match.InlineType);
        return true;
    }

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
#endif
