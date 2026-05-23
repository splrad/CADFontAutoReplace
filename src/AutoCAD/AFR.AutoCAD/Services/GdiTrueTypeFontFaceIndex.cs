using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace AFR.Services;

/// <summary>
/// Windows GDI TrueType face 按需查询器。
/// <para>
/// @TrueType 是 GDI 枚举出的 vertical face 名称；只有 EnumFontFamiliesExW 实际返回 @face 时才视为可用。
/// </para>
/// </summary>
internal static class GdiTrueTypeFontFaceIndex
{
    private const byte DefaultCharset = 1;
    private const int LfFaceSize = 32;
    private const int LfFullFaceSize = 64;

    private static readonly ConcurrentDictionary<string, bool> LookupCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> ProbeLogSeen = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsFaceAvailable(string faceName)
    {
        // GDI 枚举在 AutoCAD Regen 期间可能竞争屏幕 DC 合成锁导致主线程挂起，暂时禁用，始终返回 false。
        return false;
    }

    private static bool ProbeFaceAvailable(string faceName)
    {
        bool matched = false;
        IntPtr hdc = IntPtr.Zero;
        try
        {
            // 使用离屏内存 DC，避免与 AutoCAD Regen 期间屏幕 DC 的 DWM/GDI 合成锁冲突。
            // EnumFontFamiliesExW 在内存 DC 上的枚举结果与屏幕 DC 完全一致。
            hdc = CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return false;

            var logFont = new LOGFONTW
            {
                lfCharSet = DefaultCharset,
                lfFaceName = faceName
            };

            FontEnumProc callback = (ref ENUMLOGFONTEXW lpelfe, IntPtr _, uint __, IntPtr ___) =>
            {
                string enumerated = NormalizeFaceName(lpelfe.elfLogFont.lfFaceName);
                if (!string.Equals(enumerated, faceName, StringComparison.OrdinalIgnoreCase))
                    return 1;

                matched = true;
                return 0;
            };

            _ = EnumFontFamiliesExW(hdc, ref logFont, callback, IntPtr.Zero, 0);
            if (faceName.StartsWith("@", StringComparison.Ordinal)
                && ProbeLogSeen.TryAdd(faceName, 0))
            {
                DiagnosticLogger.Log("GDI字体",
                    $"EnumFontFamiliesExW vertical face 查询: '{faceName}' exists={matched}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError($"GDI字体 face 查询失败: '{faceName}'", ex);
        }
        finally
        {
            if (hdc != IntPtr.Zero)
                DeleteDC(hdc);
        }

        return matched;
    }

    private static string NormalizeFaceName(string faceName)
    {
        if (string.IsNullOrWhiteSpace(faceName))
            return string.Empty;

        string trimmed = faceName.Trim();
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
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
}
