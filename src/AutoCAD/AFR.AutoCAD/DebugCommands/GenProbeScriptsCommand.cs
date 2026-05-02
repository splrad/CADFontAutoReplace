#if DEBUG
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.DebugCommands.GenProbeScriptsCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// AFRGENPROBESCRIPTS 命令（仅 DEBUG）：在 <c>%TEMP%\afr-probe\</c> 生成 PowerShell 脚本，
/// 通过 <c>Assembly.LoadFrom</c> + 反射调用本 DLL 的 <c>Apply</c> / <c>Cleanup</c>，
/// 用于 AutoCAD 未运行时的回归取证（验证 DLL 自给，不依赖部署器）。
/// </summary>
public static class GenProbeScriptsCommand
{
    /// <summary>命令入口。</summary>
    [CommandMethod("AFRGENPROBESCRIPTS")]
    public static void GenerateProbeScripts()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;

        var dllPath = Assembly.GetExecutingAssembly().Location;
        var outDir = Path.Combine(Path.GetTempPath(), "afr-probe");
        Directory.CreateDirectory(outDir);

        var common = $@"$ErrorActionPreference='Stop'
$dll = '{dllPath.Replace("'", "''")}'
$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$patcher = $asm.GetType('AFR.Diagnostics.AwsHideableDialogPatcher', $true)
function Invoke-Patcher([string]$method) {{
    $m = $patcher.GetMethod($method, [System.Reflection.BindingFlags]'Public,Static')
    if ($null -eq $m) {{ throw ""method $method not found"" }}
    return $m.Invoke($null, @())
}}";

        File.WriteAllText(Path.Combine(outDir, "00-show-path.ps1"),
            common + @"
$all = Invoke-Patcher 'ListTargetAwsFiles'
Write-Host ""[AFR] candidates ($($all.Count)):""
foreach ($c in $all) { Write-Host ""  - $c"" }
$active = Invoke-Patcher 'LocateActiveAwsPath'
Write-Host ""[AFR] active: $active""
",
            new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(outDir, "01-apply.ps1"),
            common + @"
$n = Invoke-Patcher 'Apply'
Write-Host ""[AFR] Apply wrote $n file(s)""
",
            new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(outDir, "02-cleanup.ps1"),
            common + @"
$n = Invoke-Patcher 'Cleanup'
Write-Host ""[AFR] Cleanup modified $n file(s)""
",
            new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(outDir, "README.txt"),
@"AFR .aws 抑制回归取证脚本

【前提】AutoCAD 必须完全关闭，否则 Apply/Cleanup 会拒绝写入并返回 0。

【使用】
  .\00-show-path.ps1   # 列出候选 + 当前活动 .aws 路径
  .\01-apply.ps1       # 写入抑制节点（带 AFR afrToken）
  .\02-cleanup.ps1     # 仅删除带 afrToken 的节点；用户手设的同名节点不动

【说明】
  - 脚本仅做 Assembly.LoadFrom + 反射调用，所有逻辑在 DLL 内部。
  - 与正式安装/卸载流程通过同一份 Apply/Cleanup 写入，结果一致。
",
            new UTF8Encoding(false));

        ed.WriteMessage($"\n[AFR] probe scripts at: {outDir}\n");
    }
}
#endif
