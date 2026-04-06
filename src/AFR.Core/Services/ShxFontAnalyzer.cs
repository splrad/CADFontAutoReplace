using System.IO;
using System.Text;

namespace AFR.Services;

/// <summary>
/// SHX 字体文件特征码解析器。
/// <para>
/// 通过读取 SHX 文件头（前 30 字节）判断字体类型（常规字体 / 大字体）。
/// 文件头格式示例:
/// <list type="bullet">
///   <item>"AutoCAD-86 shapes 1.0" — 常规字体</item>
///   <item>"AutoCAD-86 bigfont 1.0" — 大字体</item>
///   <item>"AutoCAD-86 unifont 1.0" — Unicode 字体</item>
/// </list>
/// 使用 <see cref="FileShare.ReadWrite"/> 模式打开文件，避免 CAD 锁定文件导致读取失败。
/// </para>
/// </summary>
public static class ShxFontAnalyzer
{
    /// <summary>
    /// 判断给定的 SHX 文件是否为大字体。
    /// </summary>
    /// <param name="filePath">SHX 文件的完整路径。</param>
    /// <returns>
    /// true = 大字体，false = 常规字体，null = 文件不可读（损坏、权限不足等）。
    /// 文件不存在或大小为 0 视为确定性的非大字体，返回 false 而非 null。
    /// </returns>
    public static bool? IsBigFont(string filePath)
    {
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                return false;

            byte[] header = new byte[30];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int bytesRead = fs.Read(header, 0, 30);
            if (bytesRead < 25) return false;

            string headerStr = Encoding.ASCII.GetString(header, 0, bytesRead);
            return headerStr.Contains("bigfont", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }
}
