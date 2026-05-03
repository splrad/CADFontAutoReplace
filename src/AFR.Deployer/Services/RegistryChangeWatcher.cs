using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AFR.Deployer.Services;

/// <summary>
/// 基于 Win32 <c>RegNotifyChangeKeyValue</c> 的注册表子树异步监听器。
/// <para>
/// 在指定 HKCU 子键（含子树）发生 名称/值/属性/安全 变更时触发 <see cref="Changed"/>，
/// 用于替代轮询式扫描。事件在 ThreadPool 上回调，订阅方需自行 Marshal 到 UI 线程。
/// </para>
/// <para>
/// <b>键不存在时的回退策略：</b><see cref="RegNotifyChangeKeyValue"/> 只能监听已存在的键，
/// 因此当目标 <paramref>subKey</paramref> 尚未在本机创建（例如该 CAD 版本未安装）时，
/// 监听器会沿路径向上回退到最深的已存在祖先（不超过 <paramref>fallbackRoot</paramref>）
/// 并以子树模式监听它。一旦祖先发生变更，监听器会重新尝试打开目标键：成功后即"升级"
/// 到目标键继续精确监听，期间也会触发一次 <see cref="Changed"/>，确保 UI 能感知到键
/// 从无到有的过程。键被删除时句柄会被立刻通知一次，<see cref="Changed"/> 触发后再次
/// 自动回退到祖先继续等待。
/// </para>
/// </summary>
internal sealed class RegistryChangeWatcher : IDisposable
{
    private const int KEY_NOTIFY      = 0x0010;
    private const int KEY_WOW64_64KEY = 0x0100;

    private const int REG_NOTIFY_CHANGE_NAME       = 0x0001;
    private const int REG_NOTIFY_CHANGE_ATTRIBUTES = 0x0002;
    private const int REG_NOTIFY_CHANGE_LAST_SET   = 0x0004;
    private const int REG_NOTIFY_CHANGE_SECURITY   = 0x0008;

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyEx(
        IntPtr hKey, string subKey, int options, int samDesired, out SafeRegistryHandle phkResult);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey, bool watchSubtree, int notifyFilter,
        SafeWaitHandle hEvent, bool asynchronous);

    private readonly string                  _targetSubKey;
    private readonly string                  _fallbackRoot;
    private readonly CancellationTokenSource _cts = new();
    private readonly object                  _gate = new();

    private SafeRegistryHandle?   _keyHandle;
    private ManualResetEvent?     _signal;
    private RegisteredWaitHandle? _registration;
    private bool                  _watchingTarget;   // 当前句柄是否指向目标键

    /// <summary>注册表变更事件（ThreadPool 线程）。</summary>
    internal event Action? Changed;

    /// <param name="subKey">目标 HKCU 子键完整路径，例如 <c>Software\Autodesk\AutoCAD\R25.0</c>。</param>
    /// <param name="fallbackRoot">
    /// 当目标键不存在时回退监听的最高祖先路径，例如 <c>Software\Autodesk\AutoCAD</c>。
    /// 不应传入过宽的根（如 <c>Software</c>），否则子树监听会过于嘈杂。
    /// </param>
    internal RegistryChangeWatcher(string subKey, string fallbackRoot)
    {
        _targetSubKey = subKey;
        _fallbackRoot = fallbackRoot;
    }

    internal void Start()
    {
        _signal = new ManualResetEvent(false);

        if (!OpenBestAvailable()) return;
        if (!Arm())               return;

        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal, OnSignaled, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// 优先打开目标键；失败则沿路径向上找最深的已存在祖先（不超过 <see cref="_fallbackRoot"/>）。
    /// </summary>
    private bool OpenBestAvailable()
    {
        // 释放旧句柄（升级/降级路径切换时复用）
        _keyHandle?.Dispose();
        _keyHandle      = null;
        _watchingTarget = false;

        if (TryOpen(_targetSubKey, out var target))
        {
            _keyHandle      = target;
            _watchingTarget = true;
            return true;
        }

        // 从目标向上逐级回退，但不越过 _fallbackRoot
        var path = _targetSubKey;
        while (!string.Equals(path, _fallbackRoot, StringComparison.OrdinalIgnoreCase))
        {
            var idx = path.LastIndexOf('\\');
            if (idx <= 0) break;
            path = path[..idx];

            if (TryOpen(path, out var ancestor))
            {
                _keyHandle = ancestor;
                return true;
            }

            if (string.Equals(path, _fallbackRoot, StringComparison.OrdinalIgnoreCase))
                break;
        }

        // 最后兜底：尝试 fallbackRoot 本身
        if (TryOpen(_fallbackRoot, out var root))
        {
            _keyHandle = root;
            return true;
        }

        return false;
    }

    private static bool TryOpen(string subKey, out SafeRegistryHandle handle)
        => RegOpenKeyEx(HKEY_CURRENT_USER, subKey, 0,
            KEY_NOTIFY | KEY_WOW64_64KEY, out handle) == 0 && !handle.IsInvalid;

    private bool Arm()
    {
        if (_keyHandle is null || _signal is null || _cts.IsCancellationRequested)
            return false;

        _signal.Reset();

        const int filter = REG_NOTIFY_CHANGE_NAME
                         | REG_NOTIFY_CHANGE_ATTRIBUTES
                         | REG_NOTIFY_CHANGE_LAST_SET
                         | REG_NOTIFY_CHANGE_SECURITY;

        return RegNotifyChangeKeyValue(
            _keyHandle, watchSubtree: true, filter,
            _signal.SafeWaitHandle, asynchronous: true) == 0;
    }

    private void OnSignaled(object? state, bool timedOut)
    {
        if (_cts.IsCancellationRequested) return;

        lock (_gate)
        {
            if (_cts.IsCancellationRequested) return;

            try { Changed?.Invoke(); }
            catch { /* 订阅方异常不影响后续监听 */ }

            // 状态可能已变化：
            //  • 之前监听祖先，目标键现在已被创建 → 升级到目标键
            //  • 之前监听目标键，目标键被删除 → 句柄已失效，回退到祖先
            // 任一情况都通过重新选择"最佳可用键"统一处理。
            var targetExistsNow = TryOpen(_targetSubKey, out var probe);
            probe.Dispose();

            if (targetExistsNow != _watchingTarget)
            {
                if (!OpenBestAvailable()) return;
            }

            Arm();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _registration?.Unregister(null);
        _signal?.Set();         // 唤醒一次以释放等待
        _signal?.Dispose();
        _keyHandle?.Dispose();
        _cts.Dispose();
    }
}

