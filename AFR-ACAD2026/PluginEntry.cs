using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AFR_ACAD2026.Core;
using AFR_ACAD2026.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AFR_ACAD2026.PluginEntry))]
[assembly: CommandClass(typeof(AFR_ACAD2026.Commands.AfrCommands))]

namespace AFR_ACAD2026;

/// <summary>
/// 插件入口点，实现 IExtensionApplication。
/// 负责初始化、事件注册和生命周期管理。
/// </summary>
public class PluginEntry : IExtensionApplication
{
    public void Initialize()
    {
        var log = LogService.Instance;
        try
        {
            log.Info("AFR 插件正在初始化...");

            // 第一阶段: 注册表初始化（自动加载、路径修复、默认配置）
            AppInitializer.Initialize();

            // 第二阶段: 注册文档事件以支持多文档（MDI）
            var docMgr = AcadApp.DocumentManager;
            docMgr.DocumentCreated += OnDocumentCreated;
            docMgr.DocumentActivated += OnDocumentActivated;
            docMgr.DocumentToBeDestroyed += OnDocumentToBeDestroyed;

            // 第三阶段: 通过 Idle 事件延迟启动执行
            AcadApp.Idle += OnFirstIdle;

            log.Info("AFR 插件初始化成功。");
        }
        catch (System.Exception ex)
        {
            log.Error("插件初始化失败", ex);
        }
        finally
        {
            log.Flush();
        }
    }

    public void Terminate()
    {
        try
        {
            var docMgr = AcadApp.DocumentManager;
            docMgr.DocumentCreated -= OnDocumentCreated;
            docMgr.DocumentActivated -= OnDocumentActivated;
            docMgr.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch { }
    }

    /// <summary>
    /// 初始化后首次空闲时触发一次。
    /// 若 IsInitialized = 1，则在启动时处理当前活动文档。
    /// </summary>
    private static void OnFirstIdle(object? sender, System.EventArgs e)
    {
        AcadApp.Idle -= OnFirstIdle;
        try
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                ExecutionController.Instance.Execute(doc, "Startup");
            }
        }
        catch (System.Exception ex)
        {
            LogService.Instance.Error("启动执行失败", ex);
            LogService.Instance.Flush();
        }
    }

    private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document != null)
                ExecutionController.Instance.Execute(e.Document, "DocumentCreated");
        }
        catch (System.Exception ex)
        {
            LogService.Instance.Error("DocumentCreated 处理失败", ex);
        }
    }

    private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document != null)
                ExecutionController.Instance.Execute(e.Document, "DocumentActivated");
        }
        catch (System.Exception ex)
        {
            LogService.Instance.Error("DocumentActivated 处理失败", ex);
        }
    }

    private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document != null)
                DocumentContextManager.Instance.Remove(e.Document);
        }
        catch { }
    }
}
