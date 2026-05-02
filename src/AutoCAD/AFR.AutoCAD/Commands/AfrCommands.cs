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
/// 包含发行版命令：AFR（配置替换字体）、AFRLOG（查看日志/手动替换）。
/// DEBUG 专属命令位于 <c>AFR.DebugCommands</c> 模块。
/// </summary>
public class AfrCommands
{
    /// <summary>
    /// AFR 命令：打开字体配置界面，让用户选择 SHX/TrueType 替换字体。
    /// 保存配置后标记 IsInitialized = 1，并立即对当前文档执行一次字体替换。
    /// </summary>
    [CommandMethod("AFR")]
    public void AfrCommand()
    {
        var log = LogService.Instance;
        DiagnosticLogger.Info("命令", "AFR 命令启动");
        try
        {
            var window = new FontSelectionWindow();
            PlatformManager.Host.ShowModalWindow(window);

            if (window.DialogResult != true)
            {
                DiagnosticLogger.Info("命令", "用户取消配置");
                return;
            }

            // 保存用户选择的替换字体到注册表
            var config = ConfigService.Instance;
            config.MainFont = window.SelectedMainFont;
            config.BigFont = window.SelectedBigFont;
            config.TrueTypeFont = window.SelectedTrueTypeFont;
            config.IsInitialized = true;
            DiagnosticLogger.Info("命令",
                $"配置已保存: MainFont='{config.MainFont}' BigFont='{config.BigFont}' TrueType='{config.TrueTypeFont}'");

            // 更新 Hook 的替换字体配置，使新配置在后续字体加载时生效
            PlatformManager.FontHook.UpdateConfig();

            // 首次安装时 Hook 因无配置而跳过安装，此时 DWG 解析阶段的字体拦截不可用。
            // Hook 必须在文档打开之前安装才能生效，当前会话已无法补救。
            // 提示用户重启 CAD，使 Hook 在下次启动时读取已保存的配置并正确安装。
            if (!PlatformManager.FontHook.IsInstalled)
            {
                log.Warning("首次配置完成，请重启 AutoCAD 使字体替换完整生效。");
                DiagnosticLogger.Info("命令", "Hook 未安装，跳过执行，提示用户重启");

                System.Windows.MessageBox.Show(
                    "字体配置已保存。\n\n请重启 AutoCAD 使字体替换功能完整生效。",
                    "AFR — 首次配置",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // Hook 已安装（非首次安装，用户修改配置）→ 对当前文档执行字体替换
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
                    // 无历史结果（从未执行过自动替换）→ 走正常检测替换流程
                    contextMgr.Remove(doc);
                    ExecutionController.Instance.Execute(doc, "AFR Command");
                }
            }
        }
        catch (System.Exception ex)
        {
            log.Error("配置保存失败", ex);
            DiagnosticLogger.LogError("AFR 命令失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }

    /// <summary>
    /// AFRLOG 命令：打开字体替换日志界面。
    /// <para>
    /// 显示当前文档的缺失字体检测结果和 MText 内联修复记录。
    /// 用户可在界面中手动逐一指定替换字体（仅影响当前图纸，不写入注册表全局配置）。
    /// 每次打开时重新检测数据库，以反映 ST 命令等外部修改后的最新状态。
    /// </para>
    /// </summary>
    [CommandMethod("AFRLOG")]
    public void AfrLogCommand()
    {
        var log = LogService.Instance;
        DiagnosticLogger.Info("命令", "AFRLOG 命令启动");
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                log.Info("请先打开图纸。");
                return;
            }

            DiagnosticLogger.SetContext("Doc", System.IO.Path.GetFileName(doc.Name));

            List<FontCheckResult>? results;
            HashSet<string>? stillMissingStyleNames = null;
            Dictionary<string, (string FileName, string BigFontFileName, string TypeFace)>? currentFonts = null;

            using (doc.LockDocument())
            {
                // 每次 AFRLOG 命令使用全新检测上下文，避免缓存导致结果不准确
                var context = new FontDetectionContext(doc.Database);

                // 重新检测当前文档中的缺失字体（反映替换或 ST 命令修改后的最新状态）
                var currentMissing = FontDetector.DetectMissingFonts(context);

                // 合并策略：以存储的原始检测结果（自动替换时保存的）为基础，
                // 用当前检测结果标记哪些样式仍然缺失，这样已替换的字体也能在日志中显示
                var stored = DocumentContextManager.Instance.GetDetectionResults(doc);
                DiagnosticLogger.Info("AFRLOG",
                    $"检测完成: 存储={stored?.Count ?? 0}条 当前缺失={currentMissing.Count}条");

                if (stored != null && stored.Count > 0)
                {
                    // 有存储结果时以其为基础，确保已替换的字体也能在日志中显示
                    results = stored;
                    // 构建仍缺失的样式名集合，用于在 UI 中高亮标记
                    stillMissingStyleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < currentMissing.Count; i++)
                        stillMissingStyleNames.Add(currentMissing[i].StyleName);
                }
                else
                {
                    // 无存储结果：首次打开 AFRLOG 且未执行过自动替换，直接使用当前检测结果
                    results = currentMissing;
                    if (currentMissing.Count > 0)
                    {
                        // 全部视为未替换
                        stillMissingStyleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < currentMissing.Count; i++)
                            stillMissingStyleNames.Add(currentMissing[i].StyleName);
                    }
                }

                // 读取图纸中各样式的当前实际字体信息（替换后或 ST 命令修改后的状态）
                if (results.Count > 0)
                {
                    currentFonts = FontDetector.ReadCurrentFontAssignments(doc.Database);
                }
            }

            // 构建 ViewModel 并创建日志窗口
            var config = ConfigService.Instance;
            var inlineFixResults = DocumentContextManager.Instance.GetInlineFontFixResults(doc);
            var vm = new FontReplacementLogViewModel(
                results, config.MainFont, config.BigFont, config.TrueTypeFont,
                currentFonts, inlineFixResults, stillMissingStyleNames);

            DiagnosticLogger.Info("AFRLOG",
                $"ViewModel 构建完成: Items={vm.Items.Count} 未替换={vm.FailedCount} 已替换={vm.ReplacedCount}");

            // 注册手动替换回调：当用户在日志界面中点击"替换"时执行
            var window = new FontReplacementLogWindow(vm);
            window.ApplyReplacementsHandler = replacements =>
            {
                DiagnosticLogger.Info("AFRLOG", $"ApplyReplacementsHandler 收到 {replacements.Count} 条替换请求");
                for (int i = 0; i < replacements.Count; i++)
                {
                    var r = replacements[i];
                    DiagnosticLogger.Info("AFRLOG",
                        $"  [{i}] 样式='{r.StyleName}' Main='{r.MainFontReplacement}' Big='{r.BigFontReplacement}' IsTT={r.IsTrueType}");
                }

                using (doc.LockDocument())
                {
                    // 手动替换使用独立上下文，避免与自动替换的缓存冲突
                    var replaceContext = new FontDetectionContext(doc.Database);
                    int count = FontReplacer.ReplaceByStyleMapping(replacements, replaceContext);
                    DiagnosticLogger.Info("AFRLOG", $"ReplaceByStyleMapping 返回: {count}");
                    if (count > 0) doc.Editor.Regen();
                    return count;
                }
            };

            // 注册刷新回调：应用替换后重新检测并构建新 ViewModel
            window.RefreshHandler = () =>
            {
                List<FontCheckResult> freshResults;
                HashSet<string>? freshMissing = null;
                Dictionary<string, (string FileName, string BigFontFileName, string TypeFace)>? freshFonts = null;

                using (doc.LockDocument())
                {
                    var freshContext = new FontDetectionContext(doc.Database);
                    var currentMissing = FontDetector.DetectMissingFonts(freshContext);

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

                var freshInline = DocumentContextManager.Instance.GetInlineFontFixResults(doc);
                return new FontReplacementLogViewModel(
                    freshResults, config.MainFont, config.BigFont, config.TrueTypeFont,
                    freshFonts, freshInline, freshMissing);
            };

            PlatformManager.Host.ShowModalWindow(window);

            DiagnosticLogger.Info("AFRLOG", $"窗口关闭: AppliedCount={window.AppliedCount}");

            if (window.LastAppliedReplacements != null && window.LastAppliedReplacements.Count > 0)
            {
                log.AddReplacementStatistics(window.LastAppliedReplacements);
            }
        }
        catch (System.Exception ex)
        {
            log.Error("日志查看失败", ex);
            DiagnosticLogger.LogError("AFRLOG 命令失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }

    /// <summary>
    /// 用新配置的替换字体重新覆盖已替换过的样式。
    /// <para>
    /// 复用存储的原始检测结果构建 <see cref="StyleFontReplacement"/> 列表，
    /// 通过 <see cref="FontReplacer.ReplaceByStyleMapping"/> 直接按样式名覆盖字体，
    /// 绕过缺失检测（因为旧替换字体已可用，重新检测会误判为"不缺失"）。
    /// 不包含 MText 内联字体扫描。
    /// </para>
    /// </summary>
    private static void ReapplyWithNewConfig(
        Autodesk.AutoCAD.ApplicationServices.Document doc,
        List<FontCheckResult> storedResults,
        ConfigService config,
        LogService log)
    {
        DiagnosticLogger.Info("命令", $"用新配置重新替换 {storedResults.Count} 个样式");

        using (doc.LockDocument())
        {
            var context = new FontDetectionContext(doc.Database);

            // 读取数据库中各样式当前实际字体，用于与新配置比较，跳过未变更的字体
            var currentFonts = FontDetector.ReadCurrentFontAssignments(doc.Database);

            // 将原始检测结果转换为 StyleFontReplacement 列表
            // 仅当新配置的字体与当前实际字体不同时才纳入替换
            var replacements = new List<StyleFontReplacement>();
            for (int i = 0; i < storedResults.Count; i++)
            {
                var r = storedResults[i];
                currentFonts.TryGetValue(r.StyleName, out var current);

                if (r.IsTrueType)
                {
                    if (r.IsMainFontMissing && !string.IsNullOrEmpty(config.TrueTypeFont)
                        && !string.Equals(config.TrueTypeFont, current.TypeFace, StringComparison.OrdinalIgnoreCase))
                        replacements.Add(new StyleFontReplacement(r.StyleName, true, config.TrueTypeFont, string.Empty));
                }
                else
                {
                    // 逐槽位判断：仅当新配置字体与当前实际字体不同时才填入替换值
                    string mainFont = (r.IsMainFontMissing && !string.IsNullOrEmpty(config.MainFont)
                        && !string.Equals(config.MainFont, current.FileName, StringComparison.OrdinalIgnoreCase))
                        ? config.MainFont : string.Empty;
                    string bigFont = (r.IsBigFontMissing && !string.IsNullOrEmpty(config.BigFont)
                        && !string.Equals(config.BigFont, current.BigFontFileName, StringComparison.OrdinalIgnoreCase))
                        ? config.BigFont : string.Empty;

                    // 主字体和大字体都未变更时跳过该样式
                    if (!string.IsNullOrEmpty(mainFont) || !string.IsNullOrEmpty(bigFont))
                        replacements.Add(new StyleFontReplacement(r.StyleName, false, mainFont, bigFont));
                }
            }

            if (replacements.Count == 0)
            {
                log.Info("未检测到需要重新替换的样式。");
                return;
            }

            DiagnosticLogger.Info("命令", $"构建 {replacements.Count} 条替换指令");
            int replaceCount = FontReplacer.ReplaceByStyleMapping(replacements, context);

            // 二次验证
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

            // 清理残留 SHX 引用
            FontReplacer.CleanupStaleShxReferences(context);

            // Regen 刷新显示
            if (replaceCount > 0) doc.Editor.Regen();

            // 输出统计（不含 MText 内联扫描）
            log.AddReplacementStatistics(replacements, stillMissingSlotCount);
            contextMgr.MarkExecuted(doc);
        }
    }
}
