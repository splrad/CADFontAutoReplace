using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using AFR.Models;
using AFR.Platform;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 处理样式表 @ 前缀缺失字体临时映射的 AcGiTextStyle Hook。
/// <para>
/// 它不改写样式表，只在样式加载时对已登记的 @ 字体做运行时映射。
/// </para>
/// </summary>
internal static class StyleTextStyleHook
{
    private const string Tag = "StyleTextStyleHook";
    private const int MaxStyleLoadLogRecords = 128;

    private static NativeInlineHook<AcGiTextStyleLoadStyleRecDelegate>? _loadStyleRecHook;
    private static AcGiTextStyleLoadStyleRecDelegate? _loadStyleRecHookDelegate;
    private static AcGiTextStyleLoadStyleRecDelegate? _loadStyleRecThunkTrampolineDelegate;
    private static AcGiTextStyleStringGetterDelegate? _styleNameGetter;
    private static AcGiTextStyleStringGetterDelegate? _fileNameGetter;
    private static AcGiTextStyleStringGetterDelegate? _bigFontFileNameGetter;
    private static AcGiTextStyleBoolGetterDelegate? _isVerticalGetter;
    private static AcGiTextStyleSetVerticalDelegate? _setVertical;
    private static AcGiTextStyleSetFontDelegate? _setFont;
    private static AcGiTextStyleSetFileNameDelegate? _setFileName;
    private static AcGiTextStyleSetFileNameDelegate? _setBigFontFileName;
    private static IntPtr _loadStyleRecThunkTarget;
    private static IntPtr _loadStyleRecThunkTrampoline;
    private static byte[]? _loadStyleRecThunkSavedBytes;
    private static bool _loadStyleRecThunkInstalled;
    private static readonly ConcurrentDictionary<string, RuntimeFontMappingRecord[]> RuntimeMappingsByStyle =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RuntimeFontMappingRecord> RuntimeApplyHits =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> StyleLoadLogSeen =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> RuntimeApplyLogSeen =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IntPtr> NativeStringCache =
        new(StringComparer.Ordinal);
    private static int _styleLoadLogCount;

    [ThreadStatic] private static bool _inLoadStyleRecHook;
    [ThreadStatic] private static int _styleRuntimeScopeDepth;

    internal static bool IsInstalled => _loadStyleRecHook?.IsInstalled == true || _loadStyleRecThunkInstalled;

    internal static bool IsInsideStyleLoad => _inLoadStyleRecHook;

    internal static bool IsInsideStyleRuntimeOperation
        => _inLoadStyleRecHook || _styleRuntimeScopeDepth > 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AcGiTextStyleLoadStyleRecDelegate(IntPtr self, IntPtr db);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AcGiTextStyleStringGetterDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool AcGiTextStyleBoolGetterDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AcGiTextStyleSetVerticalDelegate(IntPtr self, byte isVertical);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AcGiTextStyleSetFontDelegate(
        IntPtr self,
        IntPtr typeface,
        byte bold,
        byte italic,
        int charset,
        int pitch,
        int family);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AcGiTextStyleSetFileNameDelegate(IntPtr self, IntPtr fileName);

    internal static IDisposable EnterStyleRuntimeOperation()
    {
        _styleRuntimeScopeDepth++;
        return new StyleRuntimeScope();
    }

    private sealed class StyleRuntimeScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_styleRuntimeScopeDepth > 0)
                _styleRuntimeScopeDepth--;
        }
    }

    internal static void Install()
    {
        if (IsInstalled)
            return;

        if (PlatformManager.Platform is not INativeFontHookExportsProvider exports)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.DisplayName} 未提供字体 Hook 导出定义，跳过样式表字体 Hook。");
            return;
        }

        IntPtr module = GetModuleHandle(PlatformManager.Platform.AcDbDllName);
        if (module == IntPtr.Zero)
        {
            DiagnosticLogger.Log(Tag, $"{PlatformManager.Platform.AcDbDllName} 未加载，跳过样式表字体 Hook。");
            return;
        }

        TryResolveStringGetter(module, exports.AcGiTextStyleStyleNameExport, out _styleNameGetter);
        TryResolveStringGetter(module, exports.AcGiTextStyleFileNameExport, out _fileNameGetter);
        TryResolveStringGetter(module, exports.AcGiTextStyleBigFontFileNameExport, out _bigFontFileNameGetter);
        TryResolveBoolGetter(module, exports.AcGiTextStyleIsVerticalExport, out _isVerticalGetter);
        TryResolveSetVertical(module, exports.AcGiTextStyleSetVerticalExport, out _setVertical);
        TryResolveSetFont(module, exports.AcGiTextStyleSetFontExport, out _setFont);
        TryResolveSetFileName(module, exports.AcGiTextStyleSetFileNameExport, out _setFileName);
        TryResolveSetFileName(module, exports.AcGiTextStyleSetBigFontFileNameExport, out _setBigFontFileName);
        DiagnosticLogger.Log(Tag,
            $"AcGiTextStyle getter 状态: styleName={_styleNameGetter != null}, fileName={_fileNameGetter != null}, " +
            $"bigFontFileName={_bigFontFileNameGetter != null}, isVertical={_isVerticalGetter != null}, " +
            $"setFont={_setFont != null}, setFileName={_setFileName != null}, setBigFontFileName={_setBigFontFileName != null}, setVertical={_setVertical != null}");

        NativeHookTarget loadStyleRecTarget = exports.NativeFontHookProfile.AcGiTextStyleLoadStyleRec;
        if (!TryGetExportAddress(module, loadStyleRecTarget, out var address, out uint rva))
        {
            DiagnosticLogger.Log(Tag, "AcGiTextStyle::loadStyleRec 入口未通过强校验，跳过样式表字体 Hook。");
            return;
        }

        _loadStyleRecHookDelegate = LoadStyleRecHookHandler;
        if (TryInstallLoadStyleRecThunkHook(address, rva, _loadStyleRecHookDelegate, loadStyleRecTarget.ExpectedPrefix))
            return;

        _loadStyleRecHook = new NativeInlineHook<AcGiTextStyleLoadStyleRecDelegate>(
            Tag,
            "AcGiTextStyle::loadStyleRec",
            rva);

        _loadStyleRecHook.InstallAtAddress(
            address,
            rva,
            _loadStyleRecHookDelegate,
            loadStyleRecTarget.MinPrologueSize,
            loadStyleRecTarget.MaxPrologueSize,
            loadStyleRecTarget.ExpectedPrefix);
    }

    internal static void Uninstall()
    {
        _loadStyleRecHook?.Uninstall();
        _loadStyleRecHook = null;
        UninstallLoadStyleRecThunkHook();
        _loadStyleRecHookDelegate = null;
        _loadStyleRecThunkTrampolineDelegate = null;
        _styleNameGetter = null;
        _fileNameGetter = null;
        _bigFontFileNameGetter = null;
        _isVerticalGetter = null;
        _setVertical = null;
        _setFont = null;
        _setFileName = null;
        _setBigFontFileName = null;
        RuntimeMappingsByStyle.Clear();
        RuntimeApplyHits.Clear();
        FontRuntimeMappingStore.ClearStyleMappings();
        StyleLoadLogSeen.Clear();
        RuntimeApplyLogSeen.Clear();
        foreach (IntPtr ptr in NativeStringCache.Values)
        {
            try { Marshal.FreeHGlobal(ptr); } catch { }
        }
        NativeStringCache.Clear();
        _styleLoadLogCount = 0;
    }

    internal static void ReplaceStyleRuntimeFontMappings(IEnumerable<RuntimeFontMappingRecord> mappings)
    {
        RuntimeMappingsByStyle.Clear();
        RuntimeApplyHits.Clear();

        var grouped = new Dictionary<string, List<RuntimeFontMappingRecord>>(StringComparer.Ordinal);

        foreach (RuntimeFontMappingRecord mapping in mappings)
        {
            if (!string.IsNullOrWhiteSpace(mapping.StyleName))
            {
                if (!grouped.TryGetValue(mapping.StyleName, out var list))
                {
                    list = new List<RuntimeFontMappingRecord>();
                    grouped.Add(mapping.StyleName, list);
                }

                list.Add(mapping);
            }
        }

        foreach (var (styleName, list) in grouped)
        {
            RuntimeMappingsByStyle[styleName] = list.ToArray();
        }
    }

    internal static IReadOnlyList<RuntimeFontMappingRecord> GetRuntimeApplyHits()
        => RuntimeApplyHits.Values.ToArray();

    private static int LoadStyleRecHookHandler(IntPtr self, IntPtr db)
    {
        var trampoline = _loadStyleRecHook?.TrampolineDelegate ?? _loadStyleRecThunkTrampolineDelegate;
        if (trampoline == null)
            return -1;

        if (_inLoadStyleRecHook)
            return trampoline(self, db);

        bool hasStyleMappings = !RuntimeMappingsByStyle.IsEmpty;
        bool hasInlineScope = MTextInlineFontHook.IsInsideInlineFontHook;
        bool inStyleRuntimeScope = _styleRuntimeScopeDepth > 0;

        _inLoadStyleRecHook = true;
        try
        {
            RegisterObservedAtFontLoadMappings(self);
            if (hasStyleMappings)
                ApplyRegisteredStyleMappings(self);
            if (hasInlineScope)
                ApplyMTextInlineLoadStyleRecMappings(self);
            int result = trampoline(self, db);
            if (inStyleRuntimeScope || hasStyleMappings || hasInlineScope)
                LogStyleLoad(self, db, result);
            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": AcGiTextStyle::loadStyleRec Hook 异常", ex);
            return -1;
        }
        finally
        {
            _inLoadStyleRecHook = false;
        }
    }

    private static bool TryInstallLoadStyleRecThunkHook(
        IntPtr address,
        uint rva,
        AcGiTextStyleLoadStyleRecDelegate hookDelegate,
        byte[] expectedPrefix)
    {
        const int patchSize = 16;

        if (!MatchesBytes(address, expectedPrefix))
        {
            DiagnosticLogger.Log(Tag,
                $"AcGiTextStyle::loadStyleRec RVA=0x{rva:X} 入口字节不匹配，实际: {TryReadBytes(address, expectedPrefix.Length)}");
            return false;
        }

        if (!IsLoadStyleRecThunk(address))
            return false;

        try
        {
            int rel32 = Marshal.ReadInt32(address + 9);
            IntPtr jumpTarget = address + 13 + rel32;

            _loadStyleRecThunkSavedBytes = new byte[patchSize];
            Marshal.Copy(address, _loadStyleRecThunkSavedBytes, 0, patchSize);

            _loadStyleRecThunkTrampoline = NativeInlineHookInterop.VirtualAlloc(
                IntPtr.Zero,
                32,
                0x3000,
                0x40);
            if (_loadStyleRecThunkTrampoline == IntPtr.Zero)
            {
                DiagnosticLogger.Log(Tag, "AcGiTextStyle::loadStyleRec thunk trampoline 分配失败。");
                _loadStyleRecThunkSavedBytes = null;
                return false;
            }

            byte[] trampolineBytes = new byte[22];
            Array.Copy(_loadStyleRecThunkSavedBytes, 0, trampolineBytes, 0, 8);
            WriteAbsoluteJumpBytes(trampolineBytes, 8, jumpTarget);
            Marshal.Copy(trampolineBytes, 0, _loadStyleRecThunkTrampoline, trampolineBytes.Length);
            _loadStyleRecThunkTrampolineDelegate =
                Marshal.GetDelegateForFunctionPointer<AcGiTextStyleLoadStyleRecDelegate>(_loadStyleRecThunkTrampoline);

            IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(hookDelegate);
            byte[] patch = new byte[patchSize];
            for (int i = 0; i < patch.Length; i++) patch[i] = 0x90;
            WriteAbsoluteJumpBytes(patch, 0, hookPtr);

            NativeInlineHookInterop.VirtualProtect(address, patchSize, 0x40, out uint oldProtect);
            Marshal.Copy(patch, 0, address, patch.Length);
            NativeInlineHookInterop.VirtualProtect(address, patchSize, oldProtect, out _);

            _loadStyleRecThunkTarget = address;
            _loadStyleRecThunkInstalled = true;
            DiagnosticLogger.Log(Tag,
                $"AcGiTextStyle::loadStyleRec thunk Hook 安装成功。RVA=0x{rva:X}, JumpTarget=0x{jumpTarget.ToInt64():X}, PrologueSize={patchSize}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": AcGiTextStyle::loadStyleRec thunk Hook 安装失败", ex);
            UninstallLoadStyleRecThunkHook();
            return false;
        }
    }

    private static void UninstallLoadStyleRecThunkHook()
    {
        try
        {
            if (_loadStyleRecThunkInstalled
                && _loadStyleRecThunkTarget != IntPtr.Zero
                && _loadStyleRecThunkSavedBytes != null)
            {
                NativeInlineHookInterop.VirtualProtect(
                    _loadStyleRecThunkTarget,
                    (uint)_loadStyleRecThunkSavedBytes.Length,
                    0x40,
                    out uint oldProtect);
                Marshal.Copy(_loadStyleRecThunkSavedBytes, 0, _loadStyleRecThunkTarget, _loadStyleRecThunkSavedBytes.Length);
                NativeInlineHookInterop.VirtualProtect(
                    _loadStyleRecThunkTarget,
                    (uint)_loadStyleRecThunkSavedBytes.Length,
                    oldProtect,
                    out _);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError(Tag + ": AcGiTextStyle::loadStyleRec thunk Hook 卸载失败", ex);
        }
        finally
        {
            if (_loadStyleRecThunkTrampoline != IntPtr.Zero)
            {
                try { NativeInlineHookInterop.VirtualFree(_loadStyleRecThunkTrampoline, 0, 0x8000); } catch { }
            }

            _loadStyleRecThunkInstalled = false;
            _loadStyleRecThunkTarget = IntPtr.Zero;
            _loadStyleRecThunkTrampoline = IntPtr.Zero;
            _loadStyleRecThunkSavedBytes = null;
            _loadStyleRecThunkTrampolineDelegate = null;
        }
    }

    private static bool IsLoadStyleRecThunk(IntPtr address)
    {
        try
        {
            byte modRmFirst = Marshal.ReadByte(address + 2);
            byte modRmSecond = Marshal.ReadByte(address + 6);
            return Marshal.ReadByte(address) == 0x48
                   && Marshal.ReadByte(address + 1) == 0x8B
                   && (modRmFirst == 0x41 || modRmFirst == 0x49)
                   && Marshal.ReadByte(address + 3) == 0x08
                   && Marshal.ReadByte(address + 4) == 0x48
                   && Marshal.ReadByte(address + 5) == 0x8B
                   && (modRmSecond == 0x48 || modRmSecond == 0x49)
                   && Marshal.ReadByte(address + 7) == 0x08
                   && Marshal.ReadByte(address + 8) == 0xE9;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAbsoluteJumpBytes(byte[] buffer, int offset, IntPtr target)
    {
        buffer[offset] = 0xFF;
        buffer[offset + 1] = 0x25;
        buffer[offset + 2] = 0x00;
        buffer[offset + 3] = 0x00;
        buffer[offset + 4] = 0x00;
        buffer[offset + 5] = 0x00;
        BitConverter.GetBytes(target.ToInt64()).CopyTo(buffer, offset + 6);
    }

    private static bool MatchesBytes(IntPtr address, byte[] expected)
    {
        if (expected.Length == 0)
            return true;

        try
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (Marshal.ReadByte(address + i) != expected[i])
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TryReadBytes(IntPtr address, int count)
    {
        try
        {
            var buffer = new byte[count];
            Marshal.Copy(address, buffer, 0, count);
            return BitConverter.ToString(buffer).Replace('-', ' ');
        }
        catch (Exception ex)
        {
            return "<读取失败:" + ex.Message + ">";
        }
    }

    private static void LogStyleLoad(IntPtr self, IntPtr db, int result)
    {
        string styleName = ReadString(_styleNameGetter, self);
        string fileName = ReadString(_fileNameGetter, self);
        string bigFontFileName = ReadString(_bigFontFileNameGetter, self);
        bool? isVertical = ReadBool(_isVerticalGetter, self);

        RuntimeFontMappingRecord? mapping = null;
        bool registered = false;
        if (!string.IsNullOrWhiteSpace(styleName)
            && RuntimeMappingsByStyle.TryGetValue(styleName, out RuntimeFontMappingRecord[]? foundMappings)
            && foundMappings is { Length: > 0 })
        {
            registered = true;
            mapping = foundMappings[0];
        }
        bool hasAtFont = FontRedirectResolver.HasAtPrefix(fileName)
                         || FontRedirectResolver.HasAtPrefix(bigFontFileName);

        if (!ShouldLogStyleLoad(styleName, fileName, bigFontFileName, result, registered || hasAtFont))
            return;

        string mappingText = registered && mapping != null
            ? $" original='{mapping.OriginalFont}' target='{mapping.ReplacementFont}' category='{mapping.MappingCategory}' status='{mapping.Status}'"
            : string.Empty;
        string verticalText = isVertical.HasValue ? isVertical.Value.ToString() : "unknown";

        DiagnosticLogger.Log(Tag,
            $"AcGiTextStyle.loadStyleRec: style='{styleName}' result={result} file='{fileName}' " +
            $"big='{bigFontFileName}' vertical={verticalText} registered={registered}{mappingText} db=0x{db.ToInt64():X}");
    }

    private static void ApplyRegisteredStyleMappings(IntPtr self)
    {
        string styleName = ReadString(_styleNameGetter, self);
        if (string.IsNullOrWhiteSpace(styleName)
            || !RuntimeMappingsByStyle.TryGetValue(styleName, out RuntimeFontMappingRecord[]? mappings)
            || mappings.Length == 0)
        {
            return;
        }

        foreach (RuntimeFontMappingRecord mapping in mappings)
        {
            if (!IsUsableMapping(mapping))
                continue;

            if (IsTrueTypeMapping(mapping))
            {
                ApplyTrueTypeMapping(self, styleName, mapping);
            }
            else if (IsBigFontMapping(mapping))
            {
                ApplyShxMapping(self, styleName, mapping, bigFont: true);
            }
            else
            {
                ApplyShxMapping(self, styleName, mapping, bigFont: false);
            }
        }
    }

    private static bool RegisterObservedAtFontLoadMappings(IntPtr self)
    {
        string styleName = ReadString(_styleNameGetter, self);
        string fileName = ReadString(_fileNameGetter, self);
        string bigFontFileName = ReadString(_bigFontFileNameGetter, self);

        bool registered = false;
        registered |= RegisterObservedAtShxMapping(styleName, fileName, bigFont: false);
        registered |= RegisterObservedAtShxMapping(styleName, bigFontFileName, bigFont: true);
        return registered;
    }

    private static bool RegisterObservedAtShxMapping(string styleName, string fontName, bool bigFont)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return false;

        string original = NormalizeObservedShxName(fontName);
        var kind = bigFont ? FontRedirectKind.ShxBigFont : FontRedirectKind.ShxMain;
        bool semantic = bigFont || FontRedirectResolver.HasAtPrefix(original);
        FontLogicalReplacement resolution = FontRedirectResolver.ResolveLogicalFont(
            original,
            kind,
            carriesAutoCadSemantic: semantic);
        if (resolution.Action != FontLogicalReplacementAction.RuntimeLoadBridge)
            return false;

        string source = string.IsNullOrWhiteSpace(styleName)
            ? "StyleTextStyleHook:observed"
            : $"StyleTextStyleHook:observed:{styleName}";
        string displayOriginal = ResolveObservedDisplayOriginal(styleName, original, kind);

        return LdFileHook.TryRegisterResolvedAtFont(
            original,
            kind,
            source,
            null,
            displayOriginal,
            out _,
            out _);
    }

    private static string ResolveObservedDisplayOriginal(
        string styleName,
        string observedOriginal,
        FontRedirectKind kind)
    {
        if (string.IsNullOrWhiteSpace(styleName)
            || !RuntimeMappingsByStyle.TryGetValue(styleName, out RuntimeFontMappingRecord[]? mappings)
            || mappings.Length == 0)
        {
            return observedOriginal;
        }

        var matches = mappings
            .Where(mapping => IsMappingKind(mapping, kind))
            .ToArray();
        if (matches.Length == 0)
            return observedOriginal;

        string observedKey = FontRedirectResolver.GetRedirectSourceKey(observedOriginal, kind);
        for (int i = 0; i < matches.Length; i++)
        {
            string mappingKey = FontRedirectResolver.GetRedirectSourceKey(matches[i].OriginalFont, kind);
            if (string.Equals(mappingKey, observedKey, StringComparison.Ordinal))
                return matches[i].OriginalFont;
        }

        // 同一样式同一槽位正常只有一条映射；当 CAD 回调已经改写大小写时，用登记记录恢复显示原名。
        return matches.Length == 1 ? matches[0].OriginalFont : observedOriginal;
    }

    private static bool IsMappingKind(RuntimeFontMappingRecord mapping, FontRedirectKind kind)
    {
        return kind switch
        {
            FontRedirectKind.TrueType => IsTrueTypeMapping(mapping),
            FontRedirectKind.ShxBigFont => IsBigFontMapping(mapping),
            FontRedirectKind.ShxMain => !IsTrueTypeMapping(mapping) && !IsBigFontMapping(mapping),
            _ => false
        };
    }

    private static void ApplyMTextInlineLoadStyleRecMappings(IntPtr self)
    {
        string styleName = ReadString(_styleNameGetter, self);
        string fileName = ReadString(_fileNameGetter, self);
        string bigFontFileName = ReadString(_bigFontFileNameGetter, self);

        MTextInlineFontHook.TryApplyInlineLoadStyleRecMappings(
            self,
            styleName,
            fileName,
            bigFontFileName,
            GetNativeString,
            _setFileName == null ? null : _setFileName.Invoke,
            _setBigFontFileName == null ? null : _setBigFontFileName.Invoke);
    }

    private static void ApplyTrueTypeMapping(IntPtr self, string styleName, RuntimeFontMappingRecord mapping)
    {
        bool semantic = FontRedirectResolver.HasAtPrefix(mapping.OriginalFont);
        FontLogicalReplacement resolution = FontRedirectResolver.ResolveLogicalFont(
            mapping.OriginalFont,
            FontRedirectKind.TrueType,
            carriesAutoCadSemantic: semantic);

        if (resolution.Action == FontLogicalReplacementAction.RuntimeLoadBridge)
        {
            bool registered = LdFileHook.TryRegisterResolvedAtFont(
                mapping.OriginalFont,
                FontRedirectKind.TrueType,
                $"StyleTextStyleHook:{styleName}",
                null,
                mapping.OriginalFont,
                out _,
                out string ldFileReplacement);
            if (!registered)
                return;

            bool runtimeVerticalApplied = false;
            if (_setVertical != null && semantic)
            {
                _setVertical(self, 1);
                runtimeVerticalApplied = true;
            }

            RecordRuntimeApplyHit(styleName, mapping);
            LogRuntimeApply(
                styleName,
                mapping,
                runtimeVerticalApplied ? "registerLdFile+setVertical(TrueType)" : "registerLdFile(TrueType)",
                ldFileReplacement);
            return;
        }

        if (resolution.Action != FontLogicalReplacementAction.DirectLogicalReplacement)
            return;

        string replacement = FontRedirectResolver.NormalizeInputName(resolution.ReplacementName).TrimStart('@');
        if (string.IsNullOrWhiteSpace(replacement))
            return;

        bool fontApplied = false;
        bool verticalApplied = false;

        if (_setFont != null)
        {
            IntPtr replacementPtr = GetNativeString(replacement);
            _setFont(self, replacementPtr, 0, 0, 134, 2, 0);
            fontApplied = true;
        }

        if (_setVertical != null)
        {
            _setVertical(self, semantic ? (byte)1 : (byte)0);
            verticalApplied = true;
        }

        if (!fontApplied && !verticalApplied)
            return;

        RecordRuntimeApplyHit(styleName, mapping);

        string action = fontApplied && verticalApplied
            ? "setFont+setVertical(TrueType)"
            : fontApplied
                ? "setFont(TrueType)"
                : "setVertical(TrueType)";
        LogRuntimeApply(styleName, mapping, action, replacement);
    }

    private static void ApplyShxMapping(
        IntPtr self,
        string styleName,
        RuntimeFontMappingRecord mapping,
        bool bigFont)
    {
        var kind = bigFont ? FontRedirectKind.ShxBigFont : FontRedirectKind.ShxMain;
        bool semantic = bigFont || FontRedirectResolver.HasAtPrefix(mapping.OriginalFont);
        FontLogicalReplacement resolution = FontRedirectResolver.ResolveLogicalFont(
            mapping.OriginalFont,
            kind,
            carriesAutoCadSemantic: semantic);

        if (resolution.Action == FontLogicalReplacementAction.RuntimeLoadBridge)
        {
            bool registered = LdFileHook.TryRegisterResolvedAtFont(
                mapping.OriginalFont,
                kind,
                $"StyleTextStyleHook:{styleName}",
                null,
                mapping.OriginalFont,
                out _,
                out string ldFileReplacement);
            if (!registered)
                return;

            RecordRuntimeApplyHit(styleName, mapping);
            LogRuntimeApply(
                styleName,
                mapping,
                bigFont ? "registerLdFile(SHX大字体)" : "registerLdFile(SHX主字体)",
                ldFileReplacement);
            return;
        }

        if (resolution.Action != FontLogicalReplacementAction.DirectLogicalReplacement)
            return;

        string replacement = FontRedirectResolver.EnsureShx(resolution.ReplacementName);
        if (string.IsNullOrWhiteSpace(replacement))
            return;

        var setter = bigFont ? _setBigFontFileName : _setFileName;
        if (setter == null)
            return;

        setter(self, GetNativeString(replacement));
        RecordRuntimeApplyHit(styleName, mapping);
        LogRuntimeApply(styleName, mapping, bigFont ? "setBigFontFileName" : "setFileName", replacement);
    }

    private static void RecordRuntimeApplyHit(string styleName, RuntimeFontMappingRecord mapping)
    {
        string key = GetRuntimeMappingKey(styleName, mapping.OriginalFont, mapping.MappingCategory);
        RuntimeApplyHits[key] = mapping;
        FontRuntimeMappingStore.RecordStyleMapping(mapping);
    }

    private static string GetRuntimeMappingKey(string styleName, string originalFont, string category)
        => string.Concat(styleName, "\u001F", originalFont, "\u001F", category);

    private static bool IsUsableMapping(RuntimeFontMappingRecord mapping)
    {
        return !string.IsNullOrWhiteSpace(mapping.ReplacementFont)
               && !mapping.Status.Contains("需重新配置", StringComparison.OrdinalIgnoreCase)
               && !mapping.ReplacementFont.StartsWith("未找到", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrueTypeMapping(RuntimeFontMappingRecord mapping)
        => mapping.MappingCategory.Contains("TrueType", StringComparison.OrdinalIgnoreCase);

    private static bool IsBigFontMapping(RuntimeFontMappingRecord mapping)
        => mapping.MappingCategory.Contains("SHX大", StringComparison.OrdinalIgnoreCase);

    private static void LogRuntimeApply(
        string styleName,
        RuntimeFontMappingRecord mapping,
        string action,
        string appliedTarget)
    {
        string key = $"{styleName}|{mapping.OriginalFont}|{appliedTarget}|{action}";
        if (!RuntimeApplyLogSeen.TryAdd(key, 0))
            return;

        DiagnosticLogger.Log(Tag,
            $"样式表运行时映射: style='{styleName}' action={action} original='{mapping.OriginalFont}' " +
            $"target='{mapping.ReplacementFont}' applied='{appliedTarget}' category='{mapping.MappingCategory}' status='{mapping.Status}'");
    }

    private static IntPtr GetNativeString(string value)
        => NativeStringCache.GetOrAdd(value, static text => Marshal.StringToHGlobalUni(text));

    private static string NormalizeObservedShxName(string name)
    {
        string normalized = FontRedirectResolver.NormalizeInputName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        if (!string.IsNullOrEmpty(Path.GetExtension(normalized)))
            return normalized;

        return normalized + ".shx";
    }

    private static bool ShouldLogStyleLoad(
        string styleName,
        string fileName,
        string bigFontFileName,
        int result,
        bool important)
    {
        string key = $"{styleName}|{fileName}|{bigFontFileName}|{result}|{important}";
        if (!StyleLoadLogSeen.TryAdd(key, 0))
            return false;

        if (important)
            return true;

        return System.Threading.Interlocked.Increment(ref _styleLoadLogCount) <= MaxStyleLoadLogRecords;
    }

    private static string ReadString(AcGiTextStyleStringGetterDelegate? getter, IntPtr self)
    {
        if (getter == null || self == IntPtr.Zero)
            return string.Empty;

        try
        {
            IntPtr value = getter(self);
            return value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool? ReadBool(AcGiTextStyleBoolGetterDelegate? getter, IntPtr self)
    {
        if (getter == null || self == IntPtr.Zero)
            return null;

        try { return getter(self); }
        catch { return null; }
    }

    private static bool TryResolveStringGetter(
        IntPtr module,
        string exportName,
        out AcGiTextStyleStringGetterDelegate? getter)
    {
        getter = null;
        IntPtr address = NativeInlineHookInterop.GetProcAddress(module, exportName);
        if (address == IntPtr.Zero)
            return false;

        getter = Marshal.GetDelegateForFunctionPointer<AcGiTextStyleStringGetterDelegate>(address);
        return true;
    }

    private static bool TryResolveBoolGetter(
        IntPtr module,
        string exportName,
        out AcGiTextStyleBoolGetterDelegate? getter)
    {
        getter = null;
        IntPtr address = NativeInlineHookInterop.GetProcAddress(module, exportName);
        if (address == IntPtr.Zero)
            return false;

        getter = Marshal.GetDelegateForFunctionPointer<AcGiTextStyleBoolGetterDelegate>(address);
        return true;
    }

    private static bool TryResolveSetVertical(
        IntPtr module,
        string exportName,
        out AcGiTextStyleSetVerticalDelegate? setter)
    {
        setter = null;
        IntPtr address = NativeInlineHookInterop.GetProcAddress(module, exportName);
        if (address == IntPtr.Zero)
            return false;

        setter = Marshal.GetDelegateForFunctionPointer<AcGiTextStyleSetVerticalDelegate>(address);
        return true;
    }

    private static bool TryResolveSetFont(
        IntPtr module,
        string exportName,
        out AcGiTextStyleSetFontDelegate? setter)
    {
        setter = null;
        IntPtr address = NativeInlineHookInterop.GetProcAddress(module, exportName);
        if (address == IntPtr.Zero)
            return false;

        setter = Marshal.GetDelegateForFunctionPointer<AcGiTextStyleSetFontDelegate>(address);
        return true;
    }

    private static bool TryResolveSetFileName(
        IntPtr module,
        string exportName,
        out AcGiTextStyleSetFileNameDelegate? setter)
    {
        setter = null;
        IntPtr address = NativeInlineHookInterop.GetProcAddress(module, exportName);
        if (address == IntPtr.Zero)
            return false;

        setter = Marshal.GetDelegateForFunctionPointer<AcGiTextStyleSetFileNameDelegate>(address);
        return true;
    }

    private static bool TryGetExportAddress(IntPtr module, NativeHookTarget target, out IntPtr address, out uint rva)
    {
        address = IntPtr.Zero;
        rva = 0;

        if (!target.IsEnabled || string.IsNullOrWhiteSpace(target.ExportName))
        {
            DiagnosticLogger.Log(Tag, $"{target.Name} 未启用：{target.DisabledReason ?? "缺少导出符号"}");
            return false;
        }

        string exportName = target.ExportName!;
        address = NativeInlineHookInterop.GetProcAddress(module, exportName);
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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
