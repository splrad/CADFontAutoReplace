using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using System.Reflection;
using AFR.Abstractions;
using AFR.Platform;
using AFR.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Hosting;

/// <summary>
/// AutoCAD 插件入口基类，实现 <see cref="IExtensionApplication"/> 接口。
/// <para>
/// 负责插件的完整生命周期管理：初始化平台服务、注册文档事件、调度字体替换执行。
/// 各 CAD 版本适配壳（如 AFR-ACAD2026）继承此类，通过抽象方法提供版本特定的平台实例。
/// </para>
/// </summary>
public abstract class PluginEntryBase : IExtensionApplication
{
    // ── 延迟执行队列机制 ──
    // 文档事件（如 DocumentCreated）触发时不直接执行替换，而是入队等待 Idle 事件统一处理。
    // 这样做是因为文档事件触发时，文档可能尚未完全加载，此时操作数据库可能失败。
    private static readonly Queue<(Document? Doc, string Trigger)> _pendingExecutions = new();
    // 标记是否已注册 Idle 事件处理器，避免重复注册
    private static bool _idleHandlerRegistered;
    // 标记插件是否已卸载，卸载后不再处理任何事件
    private static volatile bool _unloaded;
    private static readonly object _scheduleLock = new();

    // 嵌入程序集缓存：HandyControl 等第三方依赖以嵌入资源形式打包在插件 DLL 中
    private static Assembly? _resolvedHandyControl;

    // ── 子类必须实现：提供版本特定的平台服务 ──

    /// <summary>创建当前 CAD 版本的平台常量实例（品牌、版本、注册表路径等）。</summary>
    protected abstract ICadPlatform CreatePlatform();

    /// <summary>创建当前 CAD 版本的字体 Hook 实现（拦截字体文件加载请求）。</summary>
    protected abstract IFontHook CreateFontHook();

    /// <summary>创建当前 CAD 版本的宿主能力实现（如模态窗口显示）。</summary>
    protected abstract ICadHost CreateHost();

    /// <summary>创建日志服务实现。默认返回 AutoCAD 命令行日志。</summary>
    protected virtual ILogService CreateLogger() => LogService.Instance;

    /// <summary>创建字体扫描器实现。默认返回 AutoCAD 字体扫描器。</summary>
    protected virtual IFontScanner CreateFontScanner() => new AutoCadFontScanner();

    // ── 嵌入程序集解析 ──

    /// <summary>
    /// 从插件主程序集的嵌入资源中加载第三方依赖（如 HandyControl）。
    /// <para>
    /// 当 .NET 运行时找不到某个程序集时会触发此回调。
    /// 使用 typeof(PluginEntryBase).Assembly 定位插件 DLL，
    /// 因为 PluginEntryBase 通过 Shared Project 编译进了最终的插件 DLL 中。
    /// </para>
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
        int totalRead = 0;
        while (totalRead < data.Length)
        {
            int read = stream.Read(data, totalRead, data.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        _resolvedHandyControl = Assembly.Load(data);
        return _resolvedHandyControl;
    }

    // ── IExtensionApplication 实现 ──

    /// <summary>
    /// AutoCAD 加载插件时调用。按阶段依次完成：
    /// 平台注册 → Hook 安装 → 注册表初始化 → 文档事件注册 → 延迟启动执行。
    /// <para>
    /// 首次加载（通过 NETLOAD 手动加载）时仅完成注册表初始化和字体部署，
    /// 跳过 Hook 安装、事件注册和执行调度，提示用户重启 CAD。
    /// </para>
    /// </summary>
    public void Initialize()
    {
        // 诊断日志仅在 Debug 构建时自动启用，用于开发调试
#if DEBUG
        DiagnosticLogger.Enable();
#endif

        // 注册嵌入程序集解析回调，使 HandyControl 等打包在 DLL 资源中的依赖可被加载
        AppDomain.CurrentDomain.AssemblyResolve += OnResolveEmbeddedAssembly;

        // 第负一阶段: 注册平台服务 — 必须最先执行，后续所有功能依赖 PlatformManager
        PlatformManager.Initialize(CreatePlatform(), CreateFontHook(), CreateHost(), CreateLogger(), CreateFontScanner());

        var log = LogService.Instance;
        try
        {
            // 第一阶段: 注册表初始化 — 检查/创建自动加载注册表项和默认配置
            // 首次安装时还会部署内嵌字体到 CAD Fonts 目录并写入默认替换字体
            bool isFirstRun = AppInitializer.Initialize();
            if (isFirstRun)
            {
                // 首次通过 NETLOAD 加载：CAD 已启动，Hook 无法拦截已加载的字体。
                // 仅完成注册表写入和字体部署，提示用户重启 CAD 后自动生效。
                try { AcadApp.SetSystemVariable("FONTMAP", ""); } catch { }
                try { AcadApp.SetSystemVariable("FONTALT", "."); } catch { }
                log.Info("首次加载完成，默认替换字体已部署。请重启 CAD 使插件自动生效。");
                log.Flush();
                return;
            }

            // ── 以下仅在非首次加载（注册表自动启动）时执行 ──

            // 第零阶段 A: 安装字体 Hook — 在字体加载前就位，才能拦截缺失字体请求
            if (PlatformManager.Platform.SupportsLdFileHook)
                PlatformManager.FontHook.Install();

            // 第零阶段 B: 预热系统字体索引 — 提前扫描可用字体，加速后续检测
            FontDetector.PrewarmSystemFonts();

            // 第二阶段: 注册文档事件 — 监听新建/关闭文档，自动触发字体替换
            var docMgr = AcadApp.DocumentManager;
            docMgr.DocumentCreated += OnDocumentCreated;
            docMgr.DocumentToBeDestroyed += OnDocumentToBeDestroyed;

            // 第三阶段: 延迟启动执行 — 对当前已打开的文档安排字体替换
            _unloaded = false;
            ScheduleExecution(null, "Startup");
        }
        catch (System.Exception ex)
        {
            log.Error("插件初始化失败", ex);
            log.Flush();
        }
    }

    /// <summary>AutoCAD 卸载插件时调用（通常在 CAD 退出时）。</summary>
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

        // 按所有权标记反向清理我们写入的外部注册表值（保留用户预设和中途手改）。
        try { ExternalRegistryDefaultsApplier.Cleanup(); } catch { }

        // 清理 FixedProfile.aws 中带 AFR 所有权标记的弹窗抑制节点。
        try { Diagnostics.AwsHideableDialogPatcher.Cleanup(); } catch { }

        PlatformManager.FontHook.Uninstall();
        DocumentContextManager.Instance.Clear();
        DiagnosticLogger.Disable();
    }

    // ── 事件处理与延迟调度 ──

    /// <summary>注销所有已注册的 AutoCAD 文档事件，防止卸载后继续触发。</summary>
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
    /// 将一次字体替换执行请求加入延迟队列，并确保 Idle 事件已注册。
    /// 实际执行会在 AutoCAD 空闲时由 <see cref="OnDeferredIdle"/> 处理。
    /// </summary>
    /// <param name="doc">目标文档（为 null 时在 Idle 处理时取活动文档）。</param>
    /// <param name="trigger">触发来源标识（如 "Startup"、"DocumentCreated"），用于日志。</param>
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
    /// Idle 事件回调：在 AutoCAD 空闲时批量处理延迟队列中的所有执行请求。
    /// 每次触发后注销自身，避免持续占用 Idle 事件。
    /// </summary>
    private static void OnDeferredIdle(object? sender, System.EventArgs e)
    {
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

    /// <summary>文档创建事件：将新建的文档加入延迟执行队列。</summary>
    private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
    {
        if (e.Document != null)
            ScheduleExecution(e.Document, "DocumentCreated");
    }

    /// <summary>文档即将销毁事件：清理该文档的执行记录和检测结果缓存。</summary>
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
