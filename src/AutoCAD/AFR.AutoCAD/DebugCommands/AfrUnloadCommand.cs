#if DEBUG
using Autodesk.AutoCAD.Runtime;
using AFR.Hosting;
using AFR.Services;

[assembly: CommandClass(typeof(AFR.DebugCommands.AfrUnloadCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// AFRUNLOAD 命令（仅 DEBUG 构建提供）：完整卸载 AFR 插件。
/// <para>
/// Release 构建已交由外部部署工具（AFR.Deployer）统一管理安装/卸载，
/// 因此正式发行 DLL 中不暴露此命令；DEBUG 构建保留它便于开发期手动复位。
/// </para>
/// <para>
/// 依次执行：注销所有事件监听 → 删除注册表自动加载条目 → 清空运行状态。
/// 卸载后插件不再随 CAD 启动自动加载，用户可通过 NETLOAD 命令重新加载。
/// </para>
/// </summary>
public class AfrUnloadCommand
{
    /// <summary>AFRUNLOAD 命令入口。</summary>
    [CommandMethod(AFR.Constants.CommandNames.Unload)]
    public void Execute()
    {
        var log = LogService.Instance;
        DiagnosticLogger.Info("命令", "AFRUNLOAD 命令启动");

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
#endif
