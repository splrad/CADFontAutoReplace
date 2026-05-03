using System.IO;
using AFR.Deployer.Models;
using AFR.HostIntegration;
using Microsoft.Win32;

namespace AFR.Deployer.Services;

/// <summary>
/// 部署器侧的内嵌 SHX 字体释放器。
/// <para>
/// 通过注册表（先 HKLM 再 HKCU）解析每个 CAD 配置文件实例的 <c>AcadLocation</c>，
/// 拼出 <c>&lt;AcadLocation&gt;\Fonts</c> 后调用共享的 <see cref="EmbeddedFontExtractor"/>
/// 把 Deployer EXE 内嵌的默认字体释放到磁盘。
/// </para>
/// <para>
/// 与 NETLOAD 路径下 <c>AFR.Hosting.EmbeddedFontDeployer</c> 行为一致：
/// 已存在同名文件一律跳过，不覆盖、不删除任何文件，纯增量。
/// 调用方应在确认 CAD 已关闭、注册表写入完成后再触发。
/// </para>
/// </summary>
internal static class EmbeddedFontPatcher
{
    private const string AcadLocationValueName = "AcadLocation";
    private const string FontsSubDirectory     = "Fonts";

    /// <summary>
    /// 对单个配置文件实例释放内嵌字体。任何 IO/注册表异常一律视为本次跳过（不抛出）。
    /// </summary>
    /// <returns>true 表示字体已就绪（释放成功或全部已存在）；false 表示无法定位 Fonts 目录或至少一个文件释放失败。</returns>
    public static bool Apply(CadInstallation installation)
    {
        if (!installation.IsCadInstalled) return false;

        var fontsDir = ResolveFontsDirectory(installation);
        if (fontsDir is null) return false;

        var assembly = typeof(EmbeddedFontPatcher).Assembly;
        return EmbeddedFontExtractor.ExtractAll(assembly, fontsDir, out _);
    }

    /// <summary>
    /// 从注册表解析当前配置文件实例的 <c>&lt;AcadLocation&gt;\Fonts</c>。
    /// AutoCAD 把安装路径写在每个配置文件子键里：先尝试 HKLM（标准安装），
    /// 不存在再回退 HKCU（少数版本或便携安装）。任何失败都返回 null。
    /// </summary>
    private static string? ResolveFontsDirectory(CadInstallation installation)
    {
        var subPath = $@"{installation.Descriptor.RegistryBasePath}\{installation.ProfileSubKey}";

        var acadLocation = ReadString(Registry.LocalMachine, subPath, AcadLocationValueName)
                        ?? ReadString(Registry.CurrentUser, subPath, AcadLocationValueName);

        if (string.IsNullOrWhiteSpace(acadLocation)) return null;

        try
        {
            var fontsDir = Path.Combine(acadLocation, FontsSubDirectory);
            return Directory.Exists(fontsDir) ? fontsDir : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
