using Autodesk.AutoCAD.Runtime;
using AFR.Hosting;
using AFR.FontMapping;
using AFR.Models;
using AFR.Platform;
using AFR.Services;
using AFR.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Commands;

/// <summary>
/// AFR 插件的 AutoCAD 命令定义。
/// </summary>
public class AfrCommands
{
    /// <summary>
    /// AFR：保存替换字体配置，并在 Hook 已安装时处理当前图纸。
    /// </summary>
    [CommandMethod(AFR.Constants.CommandNames.Main)]
    public void AfrCommand()
    {
        var log = LogService.Instance;
        DiagnosticLogger.Start("AfrCommands", "AfrCommand", "AFR 命令启动");
        try
        {
            var window = new FontSelectionWindow();
            PlatformManager.Host.ShowModalWindow(window);

            if (window.DialogResult != true)
            {
                DiagnosticLogger.Skip("AfrCommands", "AfrCommand", "用户取消配置");
                return;
            }

            // 配置服务会把用户选择写入注册表。
            var config = ConfigService.Instance;
            config.MainFont = window.SelectedMainFont;
            config.BigFont = window.SelectedBigFont;
            config.TrueTypeFont = window.SelectedTrueTypeFont;
            config.IsInitialized = true;
            DiagnosticLogger.Ok(
                "AfrCommands",
                "SaveConfig",
                "字体替换配置已保存",
                new Dictionary<string, object?>
                {
                    ["mainFont"] = config.MainFont,
                    ["bigFont"] = config.BigFont,
                    ["trueTypeFont"] = config.TrueTypeFont
                });

            // 刷新共享字体索引，让后续 Hook 命中使用新配置。
            PlatformManager.FontHook.UpdateConfig();

            // 首次安装时 Hook 因无配置而跳过安装，此时 DWG 解析阶段的字体拦截不可用。
            // Hook 必须在文档打开之前安装才能生效，当前会话已无法补救。
            // 提示用户重启 CAD，使 Hook 在下次启动时读取已保存的配置并正确安装。
            if (!PlatformManager.FontHook.IsInstalled)
            {
                log.Warning("首次配置完成，请重启 AutoCAD 使字体替换完整生效。");
                DiagnosticLogger.Skip(
                    "AfrCommands",
                    "ExecuteAfterConfig",
                    "Hook 未安装，跳过当前文档执行并提示用户重启");

                System.Windows.MessageBox.Show(
                    "字体配置已保存。\n\n请重启 AutoCAD 使字体替换功能完整生效。",
                    "AFR — 首次配置",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // Hook 已安装时，当前图纸可立即按新配置处理样式表。
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                var contextMgr = DocumentContextManager.Instance;
                var storedResults = contextMgr.GetDetectionResults(doc);

                if (storedResults != null && storedResults.Count > 0)
                {
                    // 有历史检测结果 → 样式已被替换为旧字体，重新检测会误判为"不缺失"。
                    // 复用存储的原始检测结果，用新配置的字体重新覆盖这些样式。
                    ReapplyWithNewConfig(doc, storedResults, config, log);
                }
                else
                {
                    // 未自动处理过的图纸走完整执行链路。
                    contextMgr.Remove(doc);
                    ExecutionController.Execute(doc, "AFR Command");
                }
            }
        }
        catch (System.Exception ex)
        {
            log.Error("配置保存失败", ex);
            DiagnosticLogger.Fail("AfrCommands", "AfrCommand", "AFR 命令失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }

    /// <summary>
    /// AFRLOG：查看当前图纸的检测结果、运行时映射和手动替换入口。
    /// <para>
    /// 手动替换只改当前图纸样式表，不改全局配置。
    /// </para>
    /// </summary>
    [CommandMethod(AFR.Constants.CommandNames.Log)]
    public void AfrLogCommand()
    {
        var log = LogService.Instance;
        DiagnosticLogger.Start("AfrCommands", "AfrLogCommand", "AFRLOG 命令启动");
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                log.Info("请先打开图纸。");
                DiagnosticLogger.Skip("AfrCommands", "AfrLogCommand", "当前没有打开的图纸");
                return;
            }

            DiagnosticLogger.SetContext("Doc", System.IO.Path.GetFileName(doc.Name));

            List<FontCheckResult>? results;
            HashSet<string>? stillMissingStyleNames = null;
            Dictionary<string, (string FileName, string BigFontFileName, string TypeFace)>? currentFonts = null;
            List<RuntimeFontMappingResultRecord>? runtimeFontMappings = null;
            var config = ConfigService.Instance;

            using (doc.LockDocument())
            {
                // 每次打开都重新检测，避免复用旧缓存。
                var context = new FontDetectionContext(doc.Database);

                // 运行时映射只来自 HookHandler 真实记录，不在 UI 层推导候选项。
                var currentMissing = FontDetector.DetectMissingFonts(context);
                runtimeFontMappings = DocumentContextManager.Instance.GetRuntimeFontMappingResults(doc);

                // 用原始检测结果保留“已替换过”的样式，再用当前检测结果标记仍缺失项。
                var stored = DocumentContextManager.Instance.GetDetectionResults(doc);
                DiagnosticLogger.Ok(
                    "AfrCommands",
                    "AfrLogDetect",
                    "AFRLOG 缺失字体检测完成",
                    new Dictionary<string, object?>
                    {
                        ["storedCount"] = stored?.Count ?? 0,
                        ["currentMissingCount"] = currentMissing.Count
                    });

                if (stored != null && stored.Count > 0)
                {
                    results = stored;
                    stillMissingStyleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < currentMissing.Count; i++)
                        stillMissingStyleNames.Add(currentMissing[i].StyleName);
                }
                else
                {
                    // 未自动处理过的图纸只显示当前检测结果。
                    results = currentMissing;
                    if (currentMissing.Count > 0)
                    {
                        stillMissingStyleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < currentMissing.Count; i++)
                            stillMissingStyleNames.Add(currentMissing[i].StyleName);
                    }
                }

                // 读当前样式表赋值，用于和原始缺失结果并排展示。
                if (results.Count > 0)
                {
                    currentFonts = FontDetector.ReadCurrentFontAssignments(doc.Database);
                }
            }

            runtimeFontMappings ??= DocumentContextManager.Instance.GetRuntimeFontMappingResults(doc);
            var vm = new FontReplacementLogViewModel(
                results, config.MainFont, config.BigFont, config.TrueTypeFont,
                currentFonts, runtimeFontMappings, stillMissingStyleNames);

            DiagnosticLogger.Ok(
                "AfrCommands",
                "BuildAfrLogViewModel",
                "AFRLOG ViewModel 构建完成",
                new Dictionary<string, object?>
                {
                    ["items"] = vm.Items.Count,
                    ["failedCount"] = vm.FailedCount,
                    ["replacedCount"] = vm.ReplacedCount,
                    ["fontMappingCount"] = vm.FontMappingCount
                });

            var window = new FontReplacementLogWindow(vm);
            window.ApplyReplacementsHandler = replacements =>
            {
                DiagnosticLogger.Start(
                    "AfrCommands",
                    "ApplyReplacementsHandler",
                    "AFRLOG 手动替换请求处理开始",
                    new Dictionary<string, object?> { ["replacementCount"] = replacements.Count });
                for (int i = 0; i < replacements.Count; i++)
                {
                    var r = replacements[i];
                    DiagnosticLogger.Ok(
                        "AfrCommands",
                        "ApplyReplacementRequest",
                        "AFRLOG 手动替换请求明细",
                        new Dictionary<string, object?>
                        {
                            ["index"] = i,
                            ["styleName"] = r.StyleName,
                            ["mainFont"] = r.MainFontReplacement,
                            ["bigFont"] = r.BigFontReplacement,
                            ["isTrueType"] = r.IsTrueType
                        });
                }

                using (doc.LockDocument())
                {
                    // 手动替换与自动执行隔离缓存。
                    var replaceContext = new FontDetectionContext(doc.Database);
                    int count = FontReplacer.ReplaceByStyleMapping(replacements, replaceContext);
                    DiagnosticLogger.Ok(
                        "AfrCommands",
                        "ApplyReplacementsHandler",
                        "AFRLOG 手动替换请求处理完成",
                        new Dictionary<string, object?> { ["replacedCount"] = count });
                    if (count > 0)
                        doc.Editor.Regen();
                    return count;
                }
            };

            // 手动替换后刷新窗口数据，不重新推导运行时映射。
            window.RefreshHandler = () =>
            {
                List<FontCheckResult> freshResults;
                HashSet<string>? freshMissing = null;
                Dictionary<string, (string FileName, string BigFontFileName, string TypeFace)>? freshFonts = null;
                List<RuntimeFontMappingResultRecord>? freshRuntimeMappings = null;

                using (doc.LockDocument())
                {
                    var freshContext = new FontDetectionContext(doc.Database);
                    var currentMissing = FontDetector.DetectMissingFonts(freshContext);
                    freshRuntimeMappings = DocumentContextManager.Instance.GetRuntimeFontMappingResults(doc);

                    var stored = DocumentContextManager.Instance.GetDetectionResults(doc);
                    if (stored != null && stored.Count > 0)
                    {
                        freshResults = stored;
                        freshMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < currentMissing.Count; i++)
                            freshMissing.Add(currentMissing[i].StyleName);
                    }
                    else
                    {
                        freshResults = currentMissing;
                        if (currentMissing.Count > 0)
                        {
                            freshMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < currentMissing.Count; i++)
                                freshMissing.Add(currentMissing[i].StyleName);
                        }
                    }

                    if (freshResults.Count > 0)
                        freshFonts = FontDetector.ReadCurrentFontAssignments(doc.Database);
                }

                return new FontReplacementLogViewModel(
                    freshResults, config.MainFont, config.BigFont, config.TrueTypeFont,
                    freshFonts, freshRuntimeMappings, freshMissing);
            };

            PlatformManager.Host.ShowModalWindow(window);

            DiagnosticLogger.Ok(
                "AfrCommands",
                "AfrLogWindowClosed",
                "AFRLOG 窗口已关闭",
                new Dictionary<string, object?> { ["appliedCount"] = window.AppliedCount });

            if (window.LastAppliedReplacements != null && window.LastAppliedReplacements.Count > 0)
            {
                log.AddReplacementStatistics(window.LastAppliedReplacements);
            }
        }
        catch (System.Exception ex)
        {
            log.Error("日志查看失败", ex);
            DiagnosticLogger.Fail("AfrCommands", "AfrLogCommand", "AFRLOG 命令失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }

    /// <summary>
    /// 用新配置覆盖已替换过的样式。
    /// <para>
    /// 复用原始缺失结果，避免旧替换字体已可用后被误判为“不缺失”。
    /// </para>
    /// </summary>
    private static void ReapplyWithNewConfig(
        Autodesk.AutoCAD.ApplicationServices.Document doc,
        List<FontCheckResult> storedResults,
        ConfigService config,
        LogService log)
    {
        DiagnosticLogger.Start(
            "AfrCommands",
            "ReapplyWithNewConfig",
            "用新配置重新替换样式开始",
            new Dictionary<string, object?>
            {
                ["storedResults"] = storedResults.Count,
                ["documentName"] = DocumentContextManager.ReadDocumentName(doc)
            });

        using (doc.LockDocument())
        {
            var context = new FontDetectionContext(doc.Database);

            // 只覆盖与新配置不同的槽位。
            var currentFonts = FontDetector.ReadCurrentFontAssignments(doc.Database);

            var replacements = new List<StyleFontReplacement>();
            for (int i = 0; i < storedResults.Count; i++)
            {
                var r = storedResults[i];
                currentFonts.TryGetValue(r.StyleName, out var current);

                if (r.IsTrueType)
                {
                    if (r.IsMainFontMissing && !string.IsNullOrEmpty(config.TrueTypeFont)
                        && !string.Equals(config.TrueTypeFont, current.TypeFace, StringComparison.OrdinalIgnoreCase))
                    {
                        bool preserveAtPrefix = FontRedirectResolver.HasAtPrefix(r.TypeFace)
                                                || FontRedirectResolver.HasAtPrefix(r.FileName);
                        replacements.Add(new StyleFontReplacement(
                            r.StyleName,
                            true,
                            config.TrueTypeFont,
                            string.Empty,
                            preserveAtPrefix));
                    }
                }
                else
                {
                    string mainFont = (r.IsMainFontMissing && !string.IsNullOrEmpty(config.MainFont)
                        && !string.Equals(config.MainFont, current.FileName, StringComparison.OrdinalIgnoreCase))
                        ? config.MainFont : string.Empty;
                    string bigFont = (r.IsBigFontMissing && !string.IsNullOrEmpty(config.BigFont)
                        && !string.Equals(config.BigFont, current.BigFontFileName, StringComparison.OrdinalIgnoreCase))
                        ? config.BigFont : string.Empty;

                    if (!string.IsNullOrEmpty(mainFont) || !string.IsNullOrEmpty(bigFont))
                        replacements.Add(new StyleFontReplacement(r.StyleName, false, mainFont, bigFont));
                }
            }

            if (replacements.Count == 0)
            {
                log.Info("未检测到需要重新替换的样式。");
                DiagnosticLogger.Skip(
                    "AfrCommands",
                    "ReapplyWithNewConfig",
                    "未检测到需要重新替换的样式",
                    new Dictionary<string, object?> { ["storedResults"] = storedResults.Count });
                return;
            }

            DiagnosticLogger.Ok(
                "AfrCommands",
                "BuildReapplyRequests",
                "重新替换指令已构建",
                new Dictionary<string, object?> { ["replacementCount"] = replacements.Count });
            int replaceCount = FontReplacer.ReplaceByStyleMapping(replacements, context);

            var postContext = new FontDetectionContext(doc.Database);
            var stillMissing = FontDetector.DetectMissingFonts(postContext);
            var contextMgr = DocumentContextManager.Instance;
            contextMgr.StoreStillMissingResults(doc, stillMissing);

            int stillMissingSlotCount = 0;
            for (int i = 0; i < stillMissing.Count; i++)
            {
                if (stillMissing[i].IsMainFontMissing) stillMissingSlotCount++;
                if (stillMissing[i].IsBigFontMissing && !stillMissing[i].IsTrueType) stillMissingSlotCount++;
            }

            FontReplacer.CleanupStaleShxReferences(context);

            // 手动重写样式表后只需要一次显示刷新。
            if (replaceCount > 0) doc.Editor.Regen();

            log.AddReplacementStatistics(replacements, stillMissingSlotCount);
            contextMgr.MarkExecuted(doc);
            DiagnosticLogger.Ok(
                "AfrCommands",
                "ReapplyWithNewConfig",
                "用新配置重新替换样式完成",
                new Dictionary<string, object?>
                {
                    ["replacementCount"] = replacements.Count,
                    ["replacedCount"] = replaceCount,
                    ["stillMissingSlotCount"] = stillMissingSlotCount
                });
        }
    }
}
