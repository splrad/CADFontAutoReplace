#if DEBUG
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.Diagnostics.ValidationCommands))]

namespace AFR.Diagnostics;

/// <summary>
/// 仅 DEBUG：验证 .aws 假设的命令集合。
/// <para>命令汇总：</para>
/// <para><c>AFRGENPROBESCRIPTS</c> — 在 %TEMP%\afr-probe\ 生成 4 个 PowerShell 脚本（写入/还原/读取/路径）。</para>
/// <para><c>AFRSHOWAWSPATH</c>     — 输出当前定位到的 *Fixed_Profile.aws 路径。</para>
/// <para><c>AFRDUMPDIALOGAPI</c>   — 反射枚举 AutoCAD 程序集中名称含 "Hideable"/"Dialog" 的类型，验证 H1（运行时 API 假设）。</para>
/// </summary>
public static class ValidationCommands
{
    [CommandMethod("AFRSHOWAWSPATH")]
    public static void ShowAwsPath()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;
        var all = OfflineAwsProbe.ListAllAwsCandidates();
        ed.WriteMessage($"\n[AFR] candidates ({all.Length}):\n");
        foreach (var c in all) ed.WriteMessage($"  - {c}\n");
        var p = OfflineAwsProbe.LocateActiveAwsPath();
        ed.WriteMessage($"[AFR] active: {p ?? "(not found)"}\n");
        if (all.Length == 0)
        {
            ed.WriteMessage("[AFR] HINT: *Fixed_Profile.aws 不存在 → AutoCAD 尚未生成。\n");
            ed.WriteMessage("       OPTIONS → 配置 → 当前配置 → '另存为...' 任意名 → 关闭 OPTIONS → EXIT 退出 AutoCAD，文件即生成。\n");
        }
    }

    /// <summary>
    /// 生成 PowerShell 脚本。脚本通过 Assembly.LoadFrom + 反射调用本 DLL 的
    /// <see cref="OfflineAwsProbe"/>，从而在 AutoCAD 未运行时完成写入/还原。
    /// 这同时验证 H7：DLL 自给、部署器只是搬运工。
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
Add-Type -AssemblyName System.Xml.Linq | Out-Null
$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$probe = $asm.GetType('AFR.Diagnostics.OfflineAwsProbe', $true)
function Invoke-Probe([string]$method, [object[]]$callArgs) {{
    $m = $probe.GetMethod($method, [System.Reflection.BindingFlags]'Public,Static')
    if ($null -eq $m) {{ throw ""method $method not found"" }}
    if ($null -eq $callArgs) {{ $callArgs = @() }}
    return $m.Invoke($null, [object[]]$callArgs)
}}";

        File.WriteAllText(Path.Combine(outDir, "00-show-path.ps1"),
            common + @"
$all = Invoke-Probe 'ListAllAwsCandidates' @()
Write-Host ""[AFR] candidates ($($all.Count)):""
foreach ($c in $all) { Write-Host ""  - $c"" }
$active = Invoke-Probe 'LocateActiveAwsPath' @()
Write-Host ""[AFR] active: $active""
if ($all.Count -eq 0) {
    Write-Host ""[AFR] *Fixed_Profile.aws 不存在 → 先在 AutoCAD 内 OPTIONS → 配置 → 当前配置 → '另存为' 任意名称，再 EXIT 退出，文件即生成。""
}
",
            new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(outDir, "01-write-min-node.ps1"),
            common + @"
# 用法：.\01-write-min-node.ps1 [token]
$token = if ($args.Count -ge 1) { $args[0] } else { 'AFR-' + (Get-Date -Format 'HHmmss') }
$backup = Invoke-Probe 'WriteMinimalNode' @($token)
Write-Host ""[AFR] wrote token=$token backup=$backup""
",
            new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(outDir, "02-restore.ps1"),
            common + @"
# 用法：.\02-restore.ps1 <backupPath>
if ($args.Count -lt 1) { throw '需要 backup 路径' }
Invoke-Probe 'RestoreBackup' @($args[0])
Write-Host ""[AFR] restored from $($args[0])""
",
            new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(outDir, "03-check-token.ps1"),
            common + @"
# 用法：.\03-check-token.ps1 <token>
if ($args.Count -lt 1) { throw '需要 token' }
$has = Invoke-Probe 'ContainsToken' @($args[0])
Write-Host ""[AFR] containsToken($($args[0]))=$has""
",
            new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(outDir, "README.txt"),
@"AFR 离线 .aws 探针脚本

【目标】证伪 / 证实以下假设：
  H3：写入最小自闭合 HideableDialog 节点（不含 Preview）足以抑制 SHX 弹窗。
  H4：在 AutoCAD 未运行时写入 .aws，AutoCAD 启动+正常退出后写入是否被保留。
  H7：DLL 自给——部署器/外部进程只通过反射调用 DLL，不持有任何业务实现。

【验证 H4 的标准流程（关键：AutoCAD 必须未运行）】
  1. 完全关闭 AutoCAD
  2. .\01-write-min-node.ps1 PROBE-A          # 写入 token=PROBE-A
  3. .\03-check-token.ps1 PROBE-A             # 期望 True
  4. 启动 AutoCAD，打开任意 dwg，正常 EXIT 命令退出（让其写盘）
  5. .\03-check-token.ps1 PROBE-A
       True  -> H4 成立（外部写入被保留）
       False -> H4 被证伪（AutoCAD 退出时覆盖；只能在进程内或退出后写）

【验证 H3】
  在 H4 成立的前提下：完成步骤 1-4 后启动 AutoCAD 打开缺 SHX 的图，
  若不弹窗 -> H3 成立（最小节点足够）
  若弹窗   -> H3 被证伪（必须包含完整 Preview/TaskDialog 节点）

【还原】
  .\02-restore.ps1 <步骤 2 输出的 backup 路径>
",
            new UTF8Encoding(false));

        ed.WriteMessage($"\n[AFR] probe scripts at: {outDir}\n");
        ed.WriteMessage("[AFR] 阅读 README.txt 后按顺序执行。注意脚本必须在 AutoCAD 完全关闭后运行。\n");
    }

    /// <summary>
    /// 反射枚举当前 AppDomain 中所有 AutoCAD 相关程序集，输出名称含 "Hideable" 或 "UnresolvedFont" 的类型/成员。
    /// 用于 H1：是否存在公开运行时 API 可直接控制隐藏对话框。
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
