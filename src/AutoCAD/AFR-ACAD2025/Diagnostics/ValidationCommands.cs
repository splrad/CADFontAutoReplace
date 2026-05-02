#if DEBUG
using System;
using System.IO;
using System.Reflection;
using System.Text;
using AFR.Diagnostics;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.Diagnostics.ValidationCommands))]

namespace AFR.Diagnostics;

/// <summary>
/// 仅 DEBUG：验证 .aws 抑制路径的诊断命令集合。
/// <para>命令汇总：</para>
/// <para><c>AFRSHOWAWSPATH</c>     — 输出当前定位到的 <c>FixedProfile.aws</c> 路径与候选列表。</para>
/// <para><c>AFRDUMPDIALOGAPI</c>   — 反射枚举 AutoCAD 程序集中名称含 "Hideable"/"Dialog" 的类型，
/// 用于回归性确认 AutoCAD 仍未公开运行时抑制 API。</para>
/// </summary>
public static class ValidationCommands
{
    [CommandMethod("AFRSHOWAWSPATH")]
    public static void ShowAwsPath()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;
        var all = AwsHideableDialogPatcher.ListTargetAwsFiles();
        ed.WriteMessage($"\n[AFR] candidates ({all.Length}):\n");
        foreach (var c in all) ed.WriteMessage($"  - {c}\n");
        var p = AwsHideableDialogPatcher.LocateActiveAwsPath();
        ed.WriteMessage($"[AFR] active: {p ?? "(not found)"}\n");
        if (p != null)
        {
            var node = AwsHideableDialogPatcher.ReadDialogNodeXml(p);
            ed.WriteMessage(string.IsNullOrEmpty(node)
                ? "[AFR] HideableDialog node: (absent)\n"
                : $"[AFR] HideableDialog node:\n{node}\n");
        }
        if (all.Length == 0)
        {
            ed.WriteMessage("[AFR] HINT: FixedProfile.aws 不存在 → AutoCAD 尚未生成。\n");
            ed.WriteMessage("       OPTIONS → 配置 → 当前配置 → '另存为...' 任意名 → 关闭 OPTIONS → EXIT 退出 AutoCAD，文件即生成。\n");
        }
    }

    /// <summary>
    /// 在 %TEMP%\afr-probe\ 生成 PowerShell 脚本：通过 <c>Assembly.LoadFrom</c> + 反射调用本 DLL 的
    /// <see cref="AwsHideableDialogPatcher.Apply"/> / <see cref="AwsHideableDialogPatcher.Cleanup"/>，
    /// 用于 AutoCAD 未运行时的回归取证（验证 DLL 自给，不依赖部署器）。
    /// </summary>
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

    /// <summary>
    /// 反射枚举当前 AppDomain 中所有 AutoCAD 相关程序集，输出名称含 "Hideable"/"UnresolvedFont"/"MissingShx"
    /// 的类型/成员；用于回归确认 AutoCAD 仍未公开可控制对话框的运行时 API。
    /// </summary>
    [CommandMethod("AFRDUMPDIALOGAPI")]
    public static void DumpDialogApi()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;

        var sb = new StringBuilder();
        sb.AppendLine($"# AFR Dialog API Probe @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName;
            try { asmName = asm.GetName().Name ?? ""; } catch { continue; }
            if (!(asmName.StartsWith("Acad", StringComparison.OrdinalIgnoreCase)
                 || asmName.StartsWith("Autodesk", StringComparison.OrdinalIgnoreCase)
                 || asmName.StartsWith("AcMgd", StringComparison.OrdinalIgnoreCase)
                 || asmName.StartsWith("AcDb", StringComparison.OrdinalIgnoreCase)
                 || asmName.StartsWith("AcCore", StringComparison.OrdinalIgnoreCase)))
                continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = Array.FindAll(ex.Types, t => t != null)!; }
            catch { continue; }

            foreach (var t in types)
            {
                var name = t.FullName ?? "";
                if (name.IndexOf("Hideable", StringComparison.OrdinalIgnoreCase) < 0
                 && name.IndexOf("UnresolvedFont", StringComparison.OrdinalIgnoreCase) < 0
                 && name.IndexOf("MissingShx", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                sb.AppendLine();
                sb.AppendLine($"[{asmName}] {name}");
                try
                {
                    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance | BindingFlags.Static
                                                 | BindingFlags.DeclaredOnly))
                        sb.AppendLine($"  {m.MemberType} {m}");
                }
                catch (System.Exception ex) { sb.AppendLine($"  (members failed: {ex.Message})"); }
            }
        }

        var path = Path.Combine(Path.GetTempPath(), $"afr-dialog-api-{DateTime.Now:HHmmss}.txt");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        ed.WriteMessage($"\n[AFR] dialog api dump: {path}\n");
    }
}
#endif
