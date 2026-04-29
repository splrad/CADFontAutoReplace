using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// 启动期运行时依赖检测。
/// <para>
/// AFR 部署工具是 unpackaged WinUI 3 应用，依赖两项前置组件：
/// <list type="number">
///   <item>.NET 10 Desktop Runtime (x64)</item>
///   <item>Windows App Runtime 1.8 (x64)</item>
/// </list>
/// 实际上两者缺失时本进程根本无法进入托管入口：
/// <list type="bullet">
///   <item>Desktop Runtime 缺失 → <c>apphost.exe</c> 先弹官方下载对话框并退出。</item>
///   <item>Windows App Runtime 缺失 → <c>MddBootstrapAutoInitializer</c>（由 SDK 注入到 &lt;Module&gt; 构造）会先弹失败对话框并退出。</item>
/// </list>
/// 因此运行到 <see cref="EnsureRuntimesAvailable"/> 即可视为依赖齐全；该方法保留为
/// "防御性兜底"：仅当 <c>GetModuleHandle</c> 显示 bootstrap DLL 未加载（极少见的禁用自动初始化场景）
/// 时，才退化为弹原生对话框给出下载链接。
/// </para>
/// </summary>
internal static class RuntimeChecker
{
    private const string DotNetDownloadUrl =
        "https://dotnet.microsoft.com/download/dotnet/10.0";

    private const string WindowsAppRuntimeUrl =
        "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe";

    /// <summary>
    /// 执行运行时兜底检测；缺失则弹窗给出下载链接，并返回 false。
    /// </summary>
    internal static bool EnsureRuntimesAvailable()
    {
        // 进程已进入托管入口 ⇒ Desktop Runtime 必在位（apphost 已校验过）。
        // 仅在 Windows App Runtime bootstrap 模块未加载时才视为异常。
        if (IsBootstrapLoaded())
            return true;

        var message =
            "AFR 部署工具运行需要以下组件，请先安装后再启动：\n\n" +
            $"• Windows App Runtime 1.8 (x64)\n  下载：{WindowsAppRuntimeUrl}\n\n" +
            "如启动时还提示缺少 .NET，请同时安装：\n" +
            $"• .NET 10 桌面运行时 (x64)\n  下载：{DotNetDownloadUrl}\n\n" +
            "点击\"确定\"将打开下载页面。";

        var clicked = MessageBox(
            IntPtr.Zero,
            message,
            "AFR 部署工具 — 缺少运行时组件",
            MB_OKCANCEL | MB_ICONWARNING | MB_TOPMOST);

        if (clicked == IDOK)
            OpenUrl(WindowsAppRuntimeUrl);

        return false;
    }

    /// <summary>
    /// 检查 Windows App Runtime bootstrap 是否已被加载到当前进程。
    /// 由 SDK 注入的自动初始化器在 <c>Main</c> 之前调用 <c>MddBootstrapInitialize</c>，
    /// 成功后该 DLL 必驻留于本进程模块表。
    /// </summary>
    private static bool IsBootstrapLoaded()
        => GetModuleHandleW("Microsoft.WindowsAppRuntime.Bootstrap.dll") != IntPtr.Zero;

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 忽略：用户已看到链接文本，可手动复制
        }
    }

    // ── Win32 ──
    private const uint MB_OKCANCEL    = 0x00000001;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_TOPMOST     = 0x00040000;
    private const int  IDOK           = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);
}
