#if DEBUG
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(AFR.DebugCommands.DumpDialogApiCommand))]

namespace AFR.DebugCommands;

/// <summary>
/// AFRDUMPDIALOGAPI 命令（仅 DEBUG）：反射枚举当前 AppDomain 中所有 AutoCAD 相关程序集，
/// 输出名称含 <c>Hideable</c>/<c>UnresolvedFont</c>/<c>MissingShx</c> 的类型/成员；
/// 用于回归确认 AutoCAD 仍未公开可控制对话框的运行时 API。
/// </summary>
public static class DumpDialogApiCommand
{
    /// <summary>命令入口。</summary>
    [CommandMethod(AFR.Constants.CommandNames.DumpDialogApi)]
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
