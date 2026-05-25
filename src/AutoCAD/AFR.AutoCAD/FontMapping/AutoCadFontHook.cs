using System.Collections.Generic;
using AFR.Abstractions;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// AutoCAD 平台的 <see cref="IFontHook"/> 实现。
/// 负责初始化共享字体索引，并安装插件级持久 Hook。
/// </summary>
internal sealed class AutoCadFontHook : IFontHook
{
    /// <summary>至少一个文件级 Hook 已安装时返回 true。</summary>
    public bool IsInstalled =>
        LdFileHook.IsInstalled
        || ShpLoadHook.IsInstalled;

    /// <summary>初始化共享索引并安装 LdFileHook / ShpLoadHook。</summary>
    public void Install()
    {
        DiagnosticLogger.Start("AutoCadFontHook", "Install", "开始安装插件级持久字体 Hook");
        InitializeFontIndexes();
        InstallOne("LdFileHook", LdFileHook.Install, () => LdFileHook.IsInstalled);
        InstallOne("ShpLoadHook", ShpLoadHook.Install, () => ShpLoadHook.IsInstalled);
        DiagnosticLogger.Ok(
            "AutoCadFontHook",
            "Install",
            "插件级持久字体 Hook 安装流程完成",
            new Dictionary<string, object?> { ["isInstalled"] = IsInstalled });
    }

    private static void InstallOne(string hookName, Action install, Func<bool> isInstalled)
    {
        if (isInstalled())
        {
            DiagnosticLogger.Skip(
                "AutoCadFontHook",
                "InstallHook",
                "Hook 已安装，跳过重复安装",
                new Dictionary<string, object?> { ["hook"] = hookName });
            return;
        }

        DiagnosticLogger.Start(
            "AutoCadFontHook",
            "InstallHook",
            "开始安装子 Hook",
            new Dictionary<string, object?> { ["hook"] = hookName });
        try
        {
            install();
            if (isInstalled())
            {
                DiagnosticLogger.Ok(
                    "AutoCadFontHook",
                    "InstallHook",
                    "子 Hook 安装成功",
                    new Dictionary<string, object?> { ["hook"] = hookName });
            }
            else
            {
                DiagnosticLogger.Skip(
                    "AutoCadFontHook",
                    "InstallHook",
                    "子 Hook 未安装，见模块级诊断原因",
                    new Dictionary<string, object?> { ["hook"] = hookName });
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Fail(
                "AutoCadFontHook",
                "InstallHook",
                "子 Hook 安装异常",
                ex,
                new Dictionary<string, object?> { ["hook"] = hookName });
            throw;
        }
    }

    private static void UninstallOne(string hookName, Action uninstall, Func<bool> wasInstalled)
    {
        bool installedBefore = wasInstalled();
        if (!installedBefore)
        {
            DiagnosticLogger.Skip(
                "AutoCadFontHook",
                "UninstallHook",
                "Hook 未安装，跳过卸载",
                new Dictionary<string, object?> { ["hook"] = hookName });
            return;
        }

        DiagnosticLogger.Start(
            "AutoCadFontHook",
            "UninstallHook",
            "开始卸载子 Hook",
            new Dictionary<string, object?> { ["hook"] = hookName });
        try
        {
            uninstall();
            DiagnosticLogger.Ok(
                "AutoCadFontHook",
                "UninstallHook",
                "子 Hook 卸载完成",
                new Dictionary<string, object?>
                {
                    ["hook"] = hookName,
                    ["isInstalled"] = wasInstalled()
                });
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Fail(
                "AutoCadFontHook",
                "UninstallHook",
                "子 Hook 卸载异常",
                ex,
                new Dictionary<string, object?> { ["hook"] = hookName });
            throw;
        }
    }

    /// <summary>卸载已安装的文件级 Hook。</summary>
    public void Uninstall()
    {
        DiagnosticLogger.Start("AutoCadFontHook", "Uninstall", "开始卸载插件级持久字体 Hook");
        UninstallOne("ShpLoadHook", ShpLoadHook.Uninstall, () => ShpLoadHook.IsInstalled);
        UninstallOne("LdFileHook", LdFileHook.Uninstall, () => LdFileHook.IsInstalled);
        DiagnosticLogger.Ok(
            "AutoCadFontHook",
            "Uninstall",
            "插件级持久字体 Hook 卸载流程完成",
            new Dictionary<string, object?> { ["isInstalled"] = IsInstalled });
    }

    /// <summary>用户修改配置后刷新共享字体索引。</summary>
    public void UpdateConfig()
    {
        DiagnosticLogger.Start("AutoCadFontHook", "UpdateConfig", "开始更新共享字体索引");
        InitializeFontIndexes();
        DiagnosticLogger.Ok("AutoCadFontHook", "UpdateConfig", "共享字体索引更新完成");
    }

    private static void InitializeFontIndexes()
    {
        ShxFontAvailabilityIndex.Initialize();
        TrueTypeFontAvailabilityIndex.Initialize();
        TrueTypeFontAvailabilityIndex.RefreshAtTrueTypeResolution(ConfigService.Instance.TrueTypeFont);
        DiagnosticLogger.Ok("AutoCadFontHook", "InitializeFontIndexes", "共享字体索引初始化完成");
    }
}
