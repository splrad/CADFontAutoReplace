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
        // 优先尝试 ETW 推送式事件（真正零轮询，延迟 <100ms）。
        // 失败的常见原因：当前用户不在 Performance Log Users 组、组策略禁用 ETW、
        // WMI 服务被裁剪等——任一情况都自动回退到下一方案。
        if (TryStartTrace()) return;

        // 回退：__InstanceCreationEvent / __InstanceDeletionEvent + WITHIN。
        // 进程端依然零轮询（事件式回调），但 WMI 服务内部会以 2s 周期轮询
        // Win32_Process 表生成增量事件——这是 WMI 通用实例事件的固有机制，
        // 不是本进程的定时器。无需任何提权，是兼容性最强的兜底方案。
        TryStartInstanceEvents();
    }

    private bool TryStartTrace()
    {
        try
        {
            _startWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT ProcessName FROM Win32_ProcessStartTrace"));
            _startWatcher.EventArrived += OnTraceEvent;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT ProcessName FROM Win32_ProcessStopTrace"));
            _stopWatcher.EventArrived += OnTraceEvent;
            _stopWatcher.Start();
            return true;
        }
        catch
        {
            DisposeWatchers();
            return false;
        }
    }

    private void TryStartInstanceEvents()
    {
        try
        {
            const string create = "SELECT TargetInstance FROM __InstanceCreationEvent "
                                + "WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'";
            const string delete = "SELECT TargetInstance FROM __InstanceDeletionEvent "
                                + "WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'";

            _startWatcher = new ManagementEventWatcher(new WqlEventQuery(create));
            _startWatcher.EventArrived += OnInstanceEvent;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(new WqlEventQuery(delete));
            _stopWatcher.EventArrived += OnInstanceEvent;
            _stopWatcher.Start();
        }
        catch
        {
            // WMI 完全不可用时静默退化：UI 仍能正常工作，只是失去实时进程感知。
            DisposeWatchers();
        }
    }

    private void OnTraceEvent(object sender, EventArrivedEventArgs e)
    {
        var name = e.NewEvent?["ProcessName"] as string;
        if (name is not null && Watched.Contains(name))
        {
            try { StateChanged?.Invoke(); } catch { /* ignore */ }
        }
    }

    private void OnInstanceEvent(object sender, EventArrivedEventArgs e)
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

    public void Dispose() => DisposeWatchers();

    private void DisposeWatchers()
    {
        try { _startWatcher?.Stop(); } catch { }
        try { _stopWatcher?.Stop();  } catch { }
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
        _startWatcher = null;
        _stopWatcher  = null;
    }
}
