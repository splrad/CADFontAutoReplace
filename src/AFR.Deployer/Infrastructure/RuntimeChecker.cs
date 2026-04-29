using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AFR.Deployer.Infrastructure;

/// <summary>
/// 启动期运行时依赖检测。
/// <para>
/// AFR 部署工具是 unpackaged WinUI 3 自包含单文件应用：
/// <list type="bullet">
///   <item>.NET 10 Desktop Runtime — 已随 EXE 一并打包（self-contained），无需用户安装。</item>
///   <item>Windows App Runtime 1.8 (x64) — 仍为外置依赖，由用户安装。</item>
/// </list>
/// 因此本检测仅校验 Windows App Runtime 是否就绪：通过查询 SDK 自动注入的
/// bootstrap DLL 是否已加载到当前进程。该模块在 <c>Main</c> 之前由
/// <c>MddBootstrapAutoInitializer</c> 调用 <c>MddBootstrapInitialize</c> 完成加载，
/// 失败时 SDK 会先弹原生失败对话框并退出，本方法保留为防御性兜底。
/// </para>
/// </summary>
internal static class RuntimeChecker
{
    private const string WindowsAppRuntimeUrl =
        "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe";

    /// <summary>
    /// 执行运行时兜底检测；缺失则弹窗给出 Windows App Runtime 下载链接，并返回 false。
    /// </summary>
    internal static bool EnsureRuntimesAvailable()
    {
        // .NET 10 已 self-contained，无需检测。
        // 仅当 Windows App Runtime bootstrap 未加载时视为异常。
        if (IsBootstrapLoaded())
            return true;

        var message =
            "AFR 部署工具运行需要以下组件，请先安装后再启动：\n\n" +
            $"• Windows App Runtime 1.8 (x64)\n  下载：{WindowsAppRuntimeUrl}\n\n" +
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
