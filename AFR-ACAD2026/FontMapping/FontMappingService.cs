using System.IO;
using AFR_ACAD2026.Services;

namespace AFR_ACAD2026.FontMapping;

/// <summary>
/// 字体映射服务 — 在插件初始化阶段确保竖排大字体文件存在，
/// 解决 2004 版 DWG 图纸因 @前缀 大字体缺失导致的多行文字乱码问题。
///
/// 根因：
/// MText 内联字体码 \Fgbenor,@gbcbig|c134; 中的 @gbcbig 会被 AutoCAD
/// 作为独立文件名查找。若 @gbcbig.shx 不存在，大字体无法加载，
/// GB2312 双字节序列按单字节解释，导致中文乱码。
///
/// 修复：
/// 在插件初始化时，为每个缺失的 @前缀 大字体创建硬链接（或拷贝）
/// 指向对应的基础字体文件。例如 @gbcbig.shx → gbcbig.shx。
/// </summary>
internal static class FontMappingService
{
    private static volatile bool _initialized;
    private static readonly object _lock = new();

    /// <summary>
    /// 需要确保存在的竖排大字体映射。
    /// Key = 竖排字体文件名（@前缀），Value = 基础字体文件名。
    /// </summary>
    private static readonly (string VerticalFont, string BaseFont)[] VerticalFontMappings =
    [
        ("@gbcbig.shx", "gbcbig.shx"),
    ];

    /// <summary>
    /// 确保所有竖排大字体文件存在。
    /// 必须在任何文档打开之前调用。
    /// </summary>
    internal static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            var log = LogService.Instance;
            string fontsDir = FindFontsDirectory();

            if (string.IsNullOrEmpty(fontsDir))
            {
                log.Warning("FontMapping: 未找到 AutoCAD Fonts 目录");
                _initialized = true;
                return;
            }

            foreach (var (verticalFont, baseFont) in VerticalFontMappings)
            {
                string basePath = Path.Combine(fontsDir, baseFont);
                string vertPath = Path.Combine(fontsDir, verticalFont);

                if (!File.Exists(basePath))
                {
                    log.Warning($"FontMapping: 基础字体不存在: {basePath}");
                    continue;
                }

                if (File.Exists(vertPath))
                {
                    log.Info($"FontMapping: {verticalFont} 已存在，跳过");
                    continue;
                }

                try
                {
                    // 优先使用硬链接（不占额外磁盘空间）
                    if (CreateHardLink(vertPath, basePath))
                    {
                        log.Info($"FontMapping: 已创建硬链接 {verticalFont} → {baseFont}");
                    }
                    else
                    {
                        // 回退到文件拷贝
                        File.Copy(basePath, vertPath);
                        log.Info($"FontMapping: 已拷贝 {baseFont} → {verticalFont}");
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    log.Warning($"FontMapping: 无权限创建 {verticalFont}（需要管理员权限写入 Fonts 目录）");
                }
                catch (Exception ex)
                {
                    log.Error($"FontMapping: 创建 {verticalFont} 失败", ex);
                }
            }

            _initialized = true;
        }
    }

    /// <summary>
    /// 查找 AutoCAD Fonts 目录。
    /// </summary>
    private static string FindFontsDirectory()
    {
        try
        {
            // 通过 acdb25.dll 所在目录推断 AutoCAD 安装路径
            var acdbModule = System.Diagnostics.Process.GetCurrentProcess().Modules
                .Cast<System.Diagnostics.ProcessModule>()
                .FirstOrDefault(m => m.ModuleName?.Equals("acdb25.dll", StringComparison.OrdinalIgnoreCase) == true);

            if (acdbModule?.FileName != null)
            {
                string acadDir = Path.GetDirectoryName(acdbModule.FileName)!;
                string fontsDir = Path.Combine(acadDir, "Fonts");
                if (Directory.Exists(fontsDir))
                    return fontsDir;
            }
        }
        catch { }

        return string.Empty;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes = 0);

    /// <summary>
    /// 诊断用：查询指定字体名的当前映射结果。
    /// </summary>
    internal static string QueryMapping(string fontName)
    {
        try
        {
            return NativeFontMap.MapFontName(fontName);
        }
        catch
        {
            return fontName;
        }
    }
}
