using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AFR.Services;

/// <summary>
/// Windows GDI TrueType face 索引。
/// <para>
/// @TrueType 是 GDI 枚举出的 vertical face 名称；只有 EnumFontFamiliesExW 实际返回 @face 时才视为可用。
/// </para>
/// </summary>
internal static class GdiTrueTypeFontFaceIndex
{
    private const byte DefaultCharset = 1;
    private const int LfFaceSize = 32;
    private const int LfFullFaceSize = 64;

    private static readonly Lazy<HashSet<string>> Faces = new(BuildFaceIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, bool> LookupCache = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsFaceAvailable(string faceName)
    {
        string normalized = NormalizeFaceName(faceName);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return LookupCache.GetOrAdd(normalized, static name => Faces.Value.Contains(name));
    }

    internal static void Prewarm() => _ = Faces.Value;

    private static HashSet<string> BuildFaceIndex()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IntPtr hdc = IntPtr.Zero;
        try
        {
            hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return result;

            var logFont = new LOGFONTW
            {
                lfCharSet = DefaultCharset,
                lfFaceName = string.Empty
            };

            FontEnumProc callback = (ref ENUMLOGFONTEXW lpelfe, IntPtr _, uint __, IntPtr ___) =>
            {
                AddFace(result, lpelfe.elfLogFont.lfFaceName);
                return 1;
            };

            _ = EnumFontFamiliesExW(hdc, ref logFont, callback, IntPtr.Zero, 0);
            DiagnosticLogger.Log("GDI字体",
                $"EnumFontFamiliesExW TrueType face 索引已构建: {result.Count}项, vertical={result.Count(x => x.StartsWith("@", StringComparison.Ordinal))}项");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.LogError("GDI字体索引构建失败", ex);
        }
        finally
        {
            if (hdc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, hdc);
        }

        return result;
    }

    private static void AddFace(ISet<string> faces, string? value)
    {
        string normalized = NormalizeFaceName(value ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalized))
            faces.Add(normalized);
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesExW(
        IntPtr hdc,
        ref LOGFONTW lpLogfont,
        FontEnumProc lpProc,
        IntPtr lParam,
        uint dwFlags);
}
