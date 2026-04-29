using System;
using AFR.Deployer.Infrastructure;

namespace AFR.Deployer;

/// <summary>
/// 自定义入口点：在 WinUI 3 启动前检测 .NET Desktop Runtime 与 Windows App Runtime，
/// 缺失时弹原生 MessageBox 引导用户下载，避免出现 "找不到 Microsoft.ui.xaml.dll" 之类的系统报错。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!RuntimeChecker.EnsureRuntimesAvailable())
            return 1;

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
