#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Registry = Microsoft.Win32.Registry;
using RegistryKey = Microsoft.Win32.RegistryKey;
using RegistryValueKind = Microsoft.Win32.RegistryValueKind;
using RegistryValueOptions = Microsoft.Win32.RegistryValueOptions;

[assembly: CommandClass(typeof(AFR.Diagnostics.ProfileDumpCommand))]

namespace AFR.Diagnostics;

/// <summary>
/// 仅 DEBUG：导出 AutoCAD Profile 全量状态以定位"始终执行我的当前选择"等隐藏对话框开关。
/// <para>注册表分支：HKCU\Software\Autodesk\AutoCAD\R**\ACAD-XXXX:XXX\</para>
/// <para>磁盘 Profile 文件：%APPDATA%\Autodesk\AutoCAD*\R*\*\Support\Profiles\ 下所有文件的 SHA256 + 全量 hex。</para>
/// <para><b>关键</b>：Profile 写盘发生在 AutoCAD 进程退出时，因此 dump A 与 dump B 之间必须 <b>完全重启 AutoCAD</b>，否则两份 dump 必然相同。</para>
/// <para>正确取证步骤：</para>
/// <para>1. 触发 SHX 弹窗，勾"始终执行我的当前选择"+"忽略缺少的 SHX 文件并继续"。</para>
/// <para>2. <b>完全关闭 AutoCAD</b> → 重启 → 跑 <c>AFRDUMPPROFILE</c> 得到 dump A。</para>
/// <para>3. OPTIONS → 系统 → 隐藏消息设置 → 重新启用该对话框 → <b>完全关闭 AutoCAD</b> → 重启 → 跑 <c>AFRDUMPPROFILE</c> 得到 dump B。</para>
/// <para>4. 文本 diff A 与 B：变化的注册表值 / 文件 hex 即真实开关。</para>
/// </summary>
public static class ProfileDumpCommand
{
    /// <summary>导出注册表 + Profile 磁盘文件的全量 dump 到 %TEMP%\afr-profile-*.txt。</summary>
    [CommandMethod("AFRDUMPPROFILE")]
    public static void DumpCurrentProfile()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var ed = doc.Editor;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# AFR Profile Dump @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("# 提示：Profile 写盘发生在 AutoCAD 退出时，A/B 之间必须完全重启 AutoCAD。");
            sb.AppendLine();

            // === 1) 注册表 ===
            sb.AppendLine("########## REGISTRY ##########");
            var productPath = LocateProductRegistryPath(sb);
            if (productPath == null)
            {
                sb.AppendLine("# 未能定位 ACAD-XXXX:XXX 产品注册表路径");
            }
            else
            {
                sb.AppendLine($"# Product registry path: HKCU\\{productPath}");
                sb.AppendLine();
                using var key = Registry.CurrentUser.OpenSubKey(productPath);
                if (key != null)
                    DumpKeyRecursive(key, indent: "", sb);
                else
                    sb.AppendLine("# (无法打开该 key)");
            }

            // === 2) 磁盘 Profile 文件 ===
            sb.AppendLine();
            sb.AppendLine("########## APPDATA PROFILE FILES ##########");
            DumpAppDataProfiles(sb);

            var path = Path.Combine(Path.GetTempPath(), $"afr-profile-{DateTime.Now:HHmmss}.txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            ed.WriteMessage($"\n[AFR] Profile dumped to: {path}\n");
            ed.WriteMessage("[AFR] 注意：A/B 取证之间必须完全重启 AutoCAD，Profile 才会写盘。\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n[AFR] Profile dump failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
        }
    }

    /// <summary>
    /// 在 HKCU\Software\Autodesk\AutoCAD\ 下查找首个 ACAD-XXXX:XXX 产品节点，返回相对路径。
    /// 范围比单独 Profiles\&lt;active&gt; 更大，可覆盖 FixedProfile 等隐藏对话框可能落地的子键。
    /// </summary>
    private static string? LocateProductRegistryPath(StringBuilder log)
    {
        const string rootPath = @"Software\Autodesk\AutoCAD";
        using var root = Registry.CurrentUser.OpenSubKey(rootPath);
        if (root == null) return null;

        foreach (var rxxx in root.GetSubKeyNames())
        {
            using var rxKey = root.OpenSubKey(rxxx);
            if (rxKey == null) continue;

            foreach (var prodKey in rxKey.GetSubKeyNames())
            {
                if (!prodKey.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase)) continue;
                log.AppendLine($"# Candidate product: HKCU\\{rootPath}\\{rxxx}\\{prodKey}");
                return $@"{rootPath}\{rxxx}\{prodKey}";
            }
        }
        return null;
    }

    /// <summary>枚举 %APPDATA%\Autodesk\AutoCAD*\R*\*\Support\Profiles\ 下所有文件，输出 SHA256 + 全量 hex。</summary>
    private static void DumpAppDataProfiles(StringBuilder sb)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var autodeskRoot = Path.Combine(appData, "Autodesk");
        if (!Directory.Exists(autodeskRoot))
        {
            sb.AppendLine($"# (未找到 {autodeskRoot})");
            return;
        }

        // AutoCAD 2025 / AutoCAD 2024 / ...
        foreach (var productDir in Directory.EnumerateDirectories(autodeskRoot, "AutoCAD*", SearchOption.TopDirectoryOnly))
        {
            // R25.0 / R24.x ...
            foreach (var verDir in SafeEnumerateDirs(productDir, "R*"))
            {
                // 语言子目录 chs / enu / ...
                foreach (var langDir in SafeEnumerateDirs(verDir, "*"))
                {
                    var profilesDir = Path.Combine(langDir, "Support", "Profiles");
                    if (!Directory.Exists(profilesDir)) continue;
                    sb.AppendLine($"# Profiles dir: {profilesDir}");
                    foreach (var file in SafeEnumerateFiles(profilesDir, "*", SearchOption.AllDirectories))
                    {
                        DumpFile(file, sb);
                    }
                }
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<string> SafeEnumerateDirs(string path, string pattern)
    {
        try { return Directory.EnumerateDirectories(path, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    private static System.Collections.Generic.IEnumerable<string> SafeEnumerateFiles(string path, string pattern, SearchOption opt)
    {
        try { return Directory.EnumerateFiles(path, pattern, opt); }
        catch { return Array.Empty<string>(); }
    }

    private static void DumpFile(string file, StringBuilder sb)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            var fi = new FileInfo(file);
            sb.AppendLine();
            sb.AppendLine($"### FILE: {file}");
            sb.AppendLine($"### Size={bytes.Length}  Modified={fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}  SHA256={sha}");

            // 文本预览（如果是可读文本）
            if (LooksLikeText(bytes))
            {
                sb.AppendLine("### --- text view ---");
                sb.AppendLine(Encoding.UTF8.GetString(bytes));
            }

            // 全量 hex（限制 64KB 以防爆炸）
            sb.AppendLine("### --- hex view ---");
            int max = Math.Min(bytes.Length, 64 * 1024);
            for (int i = 0; i < max; i += 16)
            {
                int len = Math.Min(16, max - i);
                var hex = BitConverter.ToString(bytes, i, len).Replace("-", " ");
                var ascii = new StringBuilder(len);
                for (int j = 0; j < len; j++)
                {
                    byte b = bytes[i + j];
                    ascii.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                sb.AppendLine($"{i:X8}  {hex,-47}  {ascii}");
            }
            if (bytes.Length > max)
                sb.AppendLine($"... (truncated, total {bytes.Length} bytes)");
        }
        catch (System.Exception ex)
        {
            sb.AppendLine($"### FILE: {file}  (read failed: {ex.Message})");
        }
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        int sample = Math.Min(bytes.Length, 512);
        int printable = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = bytes[i];
            if (b == 0) return false;
            if (b == 9 || b == 10 || b == 13 || (b >= 0x20 && b < 0x7F) || b >= 0x80) printable++;
        }
        return sample > 0 && printable * 100 / sample > 90;
    }

    /// <summary>递归 dump 注册表键。</summary>
    private static void DumpKeyRecursive(RegistryKey key, string indent, StringBuilder sb)
    {
        foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            object? v = null;
            RegistryValueKind kind = RegistryValueKind.Unknown;
            try { v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { /* ignore */ }
            try { kind = key.GetValueKind(name); } catch { /* ignore */ }

            string displayName = name.Length == 0 ? "(Default)" : name;
            sb.AppendLine($"{indent}{displayName} : {kind} = {FormatValue(v)}");
        }

        foreach (var subName in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"{indent}[{subName}]");
            using var sub = key.OpenSubKey(subName);
            if (sub != null)
                DumpKeyRecursive(sub, indent + "  ", sb);
        }
    }

    private static string FormatValue(object? v)
    {
        if (v == null) return "(null)";
        if (v is string s) return $"\"{s}\"";
        if (v is byte[] b) return "0x" + BitConverter.ToString(b).Replace("-", "");
        if (v is string[] arr) return "[" + string.Join(", ", arr.Select(x => $"\"{x}\"")) + "]";
        return v.ToString() ?? "(null)";
    }
}
#endif
