using AFR.Hosting;
using AFR.Services;

namespace AFR.Commands;

/// <summary>
/// Hidden AFRUNLOAD entry point.
/// <para>
/// This is intentionally not registered with <c>CommandMethod</c>. Registration would put
/// the command in AutoCAD's command stack, making it eligible for command-line suggestions.
/// <see cref="PluginEntryBase"/> invokes it only after an exact UnknownCommand match.
/// </para>
/// </summary>
internal static class AfrUnloadCommand
{
    /// <summary>Runs the unload sequence for the AFR plugin.</summary>
    public static void Execute()
    {
        var log = LogService.Instance;
        DiagnosticLogger.Info("命令", "AFRUNLOAD 隐藏卸载入口启动");

        try
        {
            // 第一步：注销事件监听、卸载 Hook、清空文档跟踪和执行队列
            PluginEntryBase.Unload();

            // 第二步：删除注册表中的自动加载条目
            var config = ConfigService.Instance;
            config.DeleteAllApplicationKeys();

            log.Info("AFR 已卸载，重启 CAD 后可通过 NETLOAD 重新加载。");
            log.Flush();
        }
        catch (System.Exception ex)
        {
            log.Error("卸载失败", ex);
            log.Flush();
        }
    }
}
