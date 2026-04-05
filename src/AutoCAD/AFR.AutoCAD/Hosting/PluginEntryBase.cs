using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using System.Reflection;
using AFR.Abstractions;
using AFR.Platform;
using AFR.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Hosting;

/// <summary>
/// AutoCAD 插件入口基类，实现 IExtensionApplication。
/// 负责初始化、事件注册和生命周期管理。
/// 各版本适配壳继承此类，提供版本特定的平台实例。
/// </summary>
public abstract class PluginEntryBase : IExtensionApplication
{
    // 延迟执行队列：文档事件入队，等待 Idle 时统一处理
    private static readonly Queue<(Document? Doc, string Trigger)> _pendingExecutions = new();
    private static bool _idleHandlerRegistered;
    private static volatile bool _unloaded;
    private static readonly object _scheduleLock = new();

    // 嵌入程序集缓存（HandyControl 等第三方依赖）
    private static Assembly? _resolvedHandyControl;

    // ── 子类必须实现 ──

    /// <summary>创建当前 CAD 版本的平台常量。</summary>
    protected abstract ICadPlatform CreatePlatform();

    /// <summary>创建当前 CAD 版本的字体 Hook 实现。</summary>
    protected abstract IFontHook CreateFontHook();

    /// <summary>创建当前 CAD 版本的宿主能力实现。</summary>
    protected abstract ICadHost CreateHost();

    /// <summary>创建日志服务实现。默认返回 AutoCAD 命令行日志。</summary>
    protected virtual ILogService CreateLogger() => LogService.Instance;

    /// <summary>创建字体扫描器实现。默认返回 AutoCAD 字体扫描器。</summary>
    protected virtual IFontScanner CreateFontScanner() => new AutoCadFontScanner();

    // ── 嵌入程序集解析 ──

    /// <summary>
    /// 从插件主程序集的嵌入资源中加载第三方依赖（如 HandyControl）。
    /// 使用 typeof(PluginEntryBase).Assembly 获取插件程序集，
    /// 因为 PluginEntryBase 通过 Shared Project 编译进插件 DLL。
    /// </summary>
    private static Assembly? OnResolveEmbeddedAssembly(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name == null || !string.Equals(name, "HandyControl", StringComparison.OrdinalIgnoreCase))
            return null;

        if (_resolvedHandyControl != null) return _resolvedHandyControl;

        using var stream = typeof(PluginEntryBase).Assembly.GetManifestResourceStream(name + ".dll");
        if (stream == null) return null;

        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        _resolvedHandyControl = Assembly.Load(data);
        return _resolvedHandyControl;
    }

    // ── IExtensionApplication ──

    public void Initialize()
    {
        // 诊断日志仅在 Debug 构建时自动启用
#if DEBUG
        DiagnosticLogger.Enable();
#endif

        // 注册嵌入程序集解析（HandyControl 等第三方依赖）
        AppDomain.CurrentDomain.AssemblyResolve += OnResolveEmbeddedAssembly;

        // 第负一阶段: 注册平台 — 必须最先执行
        PlatformManager.Initialize(CreatePlatform(), CreateFontHook(), CreateHost(), CreateLogger(), CreateFontScanner());

        var log = LogService.Instance;
        try
        {
            // 第零阶段: 安装字体 Hook
            if (PlatformManager.Platform.SupportsLdFileHook)
                PlatformManager.FontHook.Install();

            // 第零阶段 B: 预热系统字体索引
            FontDetector.PrewarmSystemFonts();

            // 第一阶段: 注册表初始化
            bool isFirstRun = AppInitializer.Initialize();
            if (isFirstRun)
            {
                try { AcadApp.SetSystemVariable("FONTMAP", ""); } catch { }
                try { AcadApp.SetSystemVariable("FONTALT", "."); } catch { }
                log.Info("首次加载，请输入 AFR 命令配置替换字体。");
            }

            // 第二阶段: 注册文档事件
            var docMgr = AcadApp.DocumentManager;
            docMgr.DocumentCreated += OnDocumentCreated;
            docMgr.DocumentToBeDestroyed += OnDocumentToBeDestroyed;

            // 第三阶段: 延迟启动执行
            _unloaded = false;
            ScheduleExecution(null, "Startup");
        }
        catch (System.Exception ex)
        {
            log.Error("插件初始化失败", ex);
            log.Flush();
        }
    }

    public void Terminate()
    {
        DiagnosticLogger.Disable();
        PlatformManager.FontHook.Uninstall();
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
            UnregisterEvents();
            _pendingExecutions.Clear();
            if (_idleHandlerRegistered)
            {
                AcadApp.Idle -= OnDeferredIdle;
                _idleHandlerRegistered = false;
            }
        }

        try
        {
            AcadApp.SetSystemVariable("FONTALT", "simplex.shx");
        }
        catch { }

        PlatformManager.FontHook.Uninstall();
        DocumentContextManager.Instance.Clear();
        DiagnosticLogger.Disable();
    }

    // ── 事件与调度 ──

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

    private static void OnDeferredIdle(object? sender, System.EventArgs e)
    {
        (Document? Doc, string Trigger)[] pending;
        lock (_scheduleLock)
        {
            AcadApp.Idle -= OnDeferredIdle;
            _idleHandlerRegistered = false;
            if (_unloaded || _pendingExecutions.Count == 0) return;
            pending = _pendingExecutions.ToArray();
            _pendingExecutions.Clear();
        }

        for (int i = 0; i < pending.Length; i++)
        {
            var (doc, trigger) = pending[i];
            doc ??= AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || doc.IsDisposed) continue;

            try
            {
                ExecutionController.Instance.Execute(doc, trigger);
            }
            catch (System.Exception ex)
            {
                DiagnosticLogger.LogError($"{trigger} 延迟执行失败", ex);
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
