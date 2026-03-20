using Autodesk.AutoCAD.Runtime;
using AFR_ACAD2026.Core;
using AFR_ACAD2026.Services;
using AFR_ACAD2026.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR_ACAD2026.Commands;

/// <summary>
/// AFR 插件的 AutoCAD 命令定义。
/// </summary>
public class AfrCommands
{
    /// <summary>
    /// AFR 命令: 打开字体配置界面，保存设置，
    /// 设置 IsInitialized = 1，并对当前文档执行字体替换。
    /// </summary>
    [CommandMethod("AFR")]
    public void AfrCommand()
    {
        var log = LogService.Instance;
        try
        {
            log.Info("AFR 命令已调用。");

            var window = new FontSelectionWindow();
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(window);

            if (window.DialogResult != true)
            {
                log.Info("AFR 命令已被用户取消。");
                return;
            }

            // 保存配置
            var config = ConfigService.Instance;
            config.MainFont = window.SelectedMainFont;
            config.BigFont = window.SelectedBigFont;
            config.IsInitialized = true;

            log.Info($"配置已保存 — 主字体: '{window.SelectedMainFont}', 大字体: '{window.SelectedBigFont}'");

            // 对当前文档执行字体替换
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                // 重置执行跟踪，以便重新处理当前文档
                DocumentContextManager.Instance.Remove(doc);
                ExecutionController.Instance.Execute(doc, "AFR Command");
            }
        }
        catch (System.Exception ex)
        {
            log.Error("AFR 命令执行失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }
}
