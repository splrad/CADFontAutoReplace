using System.Runtime.InteropServices;

namespace AFR_ACAD2026.FontMapping;

/// <summary>
/// acdb25.dll 原生字体映射 API 的 P/Invoke 封装。
/// 直接调用 AutoCAD 数据库引擎的导出函数，无需汇编 Hook。
/// </summary>
internal static class NativeFontMap
{
    private const string AcDbDll = "acdb25.dll";

    /// <summary>
    /// 添加单条字体映射（等效于 acad.fmp 中的一行 "source;target"）。
    /// 原生签名: bool __cdecl addMapping(const wchar_t* src, const wchar_t* dst)
    /// </summary>
    [DllImport(AcDbDll, CallingConvention = CallingConvention.Cdecl,
               EntryPoint = "?addMapping@@YA_NPEB_W0@Z", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool AddMapping(string sourceFont, string targetFont);

    /// <summary>
    /// 查询字体映射结果。返回原生指针，由调用方手动编组。
    /// 原生签名: const wchar_t* __cdecl mapFontName(const wchar_t* name)
    /// 注意：返回的是内部指针，不可用 LPWStr 自动编组（会触发 CoTaskMemFree）。
    /// </summary>
    [DllImport(AcDbDll, CallingConvention = CallingConvention.Cdecl,
               EntryPoint = "?mapFontName@@YAPEB_WPEB_W@Z", CharSet = CharSet.Unicode)]
    private static extern nint MapFontNameNative(string fontName);

    /// <summary>
    /// 查询字体名的映射结果。
    /// 若无映射则返回原名，若原生函数返回 NULL 则返回原名。
    /// </summary>
    internal static string MapFontName(string fontName)
    {
        nint ptr = MapFontNameNative(fontName);
        if (ptr == 0) return fontName;
        return Marshal.PtrToStringUni(ptr) ?? fontName;
    }
}
