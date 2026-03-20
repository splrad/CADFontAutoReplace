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
/// 仅监听 DocumentCreated 事件，通过 Idle 延迟执行核心逻辑。
/// 不监听 DocumentActivated，避免切换图纸时重复触发。
/// </summary>
public class PluginEntry : IExtensionApplication
{
    // 延迟执行队列：文档事件入队，等待 Idle 时统一处理
    // Doc 为 null 表示需要在 Idle 时获取当前活动文档（用于 Startup）
    private static readonly Queue<(Document? Doc, string Trigger)> _pendingExecutions = new();
    private static bool _idleHandlerRegistered;
    private static volatile bool _unloaded;
    private static readonly object _scheduleLock = new();

    public void Initialize()
    {
        var log = LogService.Instance;
        try
        {
            log.Info("AFR 插件正在初始化...");

            // 第一阶段: 注册表初始化（自动加载、路径修复、默认配置）
            AppInitializer.Initialize();

            // 第二阶段: 注册文档事件以支持多文档（MDI）
            // 仅监听 DocumentCreated（新建/打开），不监听 DocumentActivated（切换）
            var docMgr = AcadApp.DocumentManager;
            docMgr.DocumentCreated += OnDocumentCreated;
            docMgr.DocumentToBeDestroyed += OnDocumentToBeDestroyed;

            // 第三阶段: 延迟启动执行 — 不在此处获取文档引用，避免使用未就绪的文档
            _unloaded = false;
            ScheduleExecution(null, "Startup");

            log.Info("AFR 插件初始化成功。");
        }
        catch (System.Exception ex)
        {
            log.Error("插件初始化失败", ex);
            log.Flush();
        }
    }

    public void Terminate()
    {
        UnregisterEvents();
    }

    /// <summary>
    /// 卸载插件：注销所有事件、清空执行队列和文档跟踪。
    /// 由 AFRUNLOAD 命令调用。
    /// </summary>
    internal static void Unload()
    {
        lock (_scheduleLock)
        {
            _unloaded = true;

            // 注销事件
            UnregisterEvents();

            // 清空延迟执行队列
            _pendingExecutions.Clear();
            if (_idleHandlerRegistered)
            {
                AcadApp.Idle -= OnDeferredIdle;
                _idleHandlerRegistered = false;
            }
        }

        // 清空文档跟踪
        DocumentContextManager.Instance.Clear();
    }

    private static void UnregisterEvents()
    {
        try
        {
            var docMgr = AcadApp.DocumentManager;
            docMgr.DocumentCreated -= OnDocumentCreated;
            docMgr.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch { }
    }

    /// <summary>
    /// 将文档执行请求加入队列，延迟到 Application.Idle 时处理。
    /// doc 为 null 时（Startup），在 Idle 回调中获取当前活动文档。
    /// </summary>
    private static void ScheduleExecution(Document? doc, string trigger)
    {
        lock (_scheduleLock)
        {
            if (_unloaded) return;

            _pendingExecutions.Enqueue((doc, trigger));
            if (!_idleHandlerRegistered)
            {
                _idleHandlerRegistered = true;
                AcadApp.Idle += OnDeferredIdle;
            }
        }
    }

    /// <summary>
    /// Idle 回调：AutoCAD 空闲时处理所有排队的文档执行请求。
    /// doc 为 null 的条目在此处解析为当前活动文档。
    /// </summary>
    private static void OnDeferredIdle(object? sender, System.EventArgs e)
    {
        // 从队列中取出所有待处理项（持锁时间最短化）
        (Document? Doc, string Trigger)[] pending;
        lock (_scheduleLock)
        {
            AcadApp.Idle -= OnDeferredIdle;
            _idleHandlerRegistered = false;

            if (_unloaded || _pendingExecutions.Count == 0) return;

            pending = [.. _pendingExecutions];
            _pendingExecutions.Clear();
        }

        for (int i = 0; i < pending.Length; i++)
        {
            var (doc, trigger) = pending[i];
            // Startup 或其他 null 情况：在 Idle 时获取当前活动文档
            doc ??= AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || doc.IsDisposed) continue;

            try
            {
                ExecutionController.Instance.Execute(doc, trigger);
            }
            catch (System.Exception ex)
            {
                LogService.Instance.Error($"{trigger} 延迟执行失败", ex);
                LogService.Instance.Flush();
            }
        }
    }

    private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
    {
        if (e.Document != null)
            ScheduleExecution(e.Document, "DocumentCreated");
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
