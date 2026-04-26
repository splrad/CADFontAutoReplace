using System.Diagnostics;

namespace AFR.Deployer.Services;

/// <summary>
/// CAD 进程互斥检测服务，防止在 CAD 运行期间执行注册表写入或 DLL 文件操作。
/// </summary>
internal static class ProcessGuardService
{
    /// <summary>所有受监控的 CAD 进程可执行文件名称（不含扩展名）。</summary>
    private static readonly string[] WatchedProcessNames =
        ["acad", "zwcad", "gcad"];

    /// <summary>
    /// 检测是否有受监控的 CAD 进程正在运行。
    /// </summary>
    /// <param name="runningNames">正在运行的进程名列表（用于 UI 显示），未检测到时为空集合。</param>
    /// <returns>true 表示至少有一个 CAD 进程正在运行。</returns>
    internal static bool IsAnyCadRunning(out IReadOnlyList<string> runningNames)
    {
        var found = new List<string>();
        foreach (var name in WatchedProcessNames)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    found.Add(name + ".exe");
            }
            catch
            {
                // 无权限枚举进程时忽略，不误报
            }
        }

        runningNames = found;
        return found.Count > 0;
    }
}
