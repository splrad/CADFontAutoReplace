using AFR.Hosting;
using AFR.Services;

namespace AFR.Commands;

/// <summary>
/// AFRUNLOAD 隐藏卸载入口。
/// <para>
/// 不注册 <c>CommandMethod</c>，避免进入命令栈和命令建议；只由 UnknownCommand 精确匹配触发。
/// </para>
/// </summary>
internal static class AfrUnloadCommand
{
    /// <summary>执行插件卸载和自动加载注册表清理。</summary>
    public static void Execute()
    {
        var log = LogService.Instance;
        DiagnosticLogger.Start("AfrUnloadCommand", "Execute", "AFRUNLOAD 隐藏卸载入口启动");

        try
        {
            PluginEntryBase.Unload();

            var config = ConfigService.Instance;
            config.DeleteAllApplicationKeys();

            log.InfoLast("AFR 已卸载，重启 CAD 后可通过 NETLOAD 重新加载。");
            DiagnosticLogger.Ok("AfrUnloadCommand", "Execute", "AFRUNLOAD 隐藏卸载完成");
            log.Flush();
        }
        catch (System.Exception ex)
        {
            log.Error("卸载失败", ex);
            DiagnosticLogger.Fail("AfrUnloadCommand", "Execute", "AFRUNLOAD 隐藏卸载失败", ex);
            log.Flush();
        }
    }
}
