using System;
using System.Collections.Generic;
using System.Management;

namespace AFR.Deployer.Services;

/// <summary>
/// 基于 WMI <c>Win32_ProcessStartTrace</c> / <c>Win32_ProcessStopTrace</c> 的 CAD 进程实时监听器。
/// <para>
/// 仅当受监控的 CAD 进程（acad/zwcad/gcad）启动或退出时才触发 <see cref="StateChanged"/>，
/// 替代每 2 秒一次的轮询。事件在 WMI 回调线程上触发，订阅方需 Marshal 到 UI 线程。
/// </para>
/// </summary>
internal sealed class CadProcessWatcher : IDisposable
{
    private static readonly HashSet<string> Watched = new(StringComparer.OrdinalIgnoreCase)
    {
        "acad.exe", "zwcad.exe", "gcad.exe",
    };

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;

    /// <summary>受监控 CAD 进程启动或退出时触发。</summary>
    internal event Action? StateChanged;

    internal void Start()
    {
        try
        {
            // 使用 __InstanceCreationEvent / __InstanceDeletionEvent + WITHIN，
            // 无需管理员权限即可工作；WMI 会在内部按 2s 轮询，UI 端零轮询。
            const string create = "SELECT TargetInstance FROM __InstanceCreationEvent "
                                + "WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'";
            const string delete = "SELECT TargetInstance FROM __InstanceDeletionEvent "
                                + "WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'";

            _startWatcher = new ManagementEventWatcher(new WqlEventQuery(create));
            _startWatcher.EventArrived += OnEvent;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(new WqlEventQuery(delete));
            _stopWatcher.EventArrived += OnEvent;
            _stopWatcher.Start();
        }
        catch
        {
            // WMI 不可用时静默退化：UI 仍能正常工作，只是失去实时进程感知。
            Dispose();
        }
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        if (e.NewEvent?["TargetInstance"] is not ManagementBaseObject target) return;
        var name = target["Name"] as string;
        if (name is not null && Watched.Contains(name))
        {
            try { StateChanged?.Invoke(); } catch { /* ignore */ }
        }
    }

    /// <summary>同步查询当前是否有受监控 CAD 进程在运行（用于初始状态）。</summary>
    internal static bool IsAnyRunning(out IReadOnlyList<string> runningNames)
        => ProcessGuardService.IsAnyCadRunning(out runningNames);

    public void Dispose()
    {
        try { _startWatcher?.Stop(); } catch { }
        try { _stopWatcher?.Stop();  } catch { }
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
        _startWatcher = null;
        _stopWatcher  = null;
    }
}
