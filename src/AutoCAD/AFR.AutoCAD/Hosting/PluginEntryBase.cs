using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using System.Diagnostics;
using System.Reflection;
using AFR.Abstractions;
using AFR.Commands;
using AFR.Constants;
using AFR.Platform;
using AFR.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AFR.Hosting;

/// <summary>
/// AutoCAD 插件入口基类。
/// <para>
/// 各版本壳只提供平台差异；公共流程在这里完成初始化、Hook 安装和延迟执行调度。
/// </para>
/// </summary>
public abstract class PluginEntryBase : IExtensionApplication
{
    // 文档事件发生时图纸可能尚未加载完成，先入队，等 Idle 再处理数据库。
    private static readonly Queue<(Document? Doc, string Trigger)> _pendingExecutions = new();
    // 避免重复注册 Idle 处理器。
    private static bool _idleHandlerRegistered;
    // 卸载后忽略所有延迟请求。
    private static volatile bool _unloaded;
    // 重入防护：ExecutionController.Execute 内部会调用 Regen()，Regen() 会泵送
    // Windows 消息队列，可能再次触发 Idle。重入请求留到下一轮，避免跨文档状态污染和嵌套锁。
    private static bool _executeInProgress;
#if NET9_0_OR_GREATER
    private static readonly System.Threading.Lock _scheduleLock = new();
    private static readonly System.Threading.Lock _hiddenUnloadLock = new();
#else
    private static readonly object _scheduleLock = new();
    private static readonly object _hiddenUnloadLock = new();
#endif
    private static readonly HashSet<Document> _hiddenUnloadDocuments = [];
    private static bool _hiddenUnloadRegistered;
    private static bool _hiddenUnloadInProgress;
    private const string FontAltVariableName = "FONTALT";
    private const string DisabledFontAltValue = ".";

    // 第三方托管依赖以嵌入资源形式打包在插件 DLL 中。
    private static readonly Dictionary<string, Assembly> ResolvedEmbeddedAssemblies = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 从插件主程序集的嵌入资源中加载第三方托管依赖。
    /// <para>
    /// PluginEntryBase 通过 Shared Project 编进最终插件 DLL，所以资源从该程序集读取。
    /// </para>
    /// </summary>
    private static Assembly? OnResolveEmbeddedAssembly(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name == null || !IsEmbeddedDependency(name))
            return null;

        if (ResolvedEmbeddedAssemblies.TryGetValue(name, out Assembly? cached))
            return cached;

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
        Assembly assembly = Assembly.Load(data);
        ResolvedEmbeddedAssemblies[name] = assembly;
        return assembly;
    }

    private static bool IsEmbeddedDependency(string name)
    {
        return string.Equals(name, "HandyControl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AutoCAD 加载插件时调用。
    /// <para>
    /// 正常自动加载会安装 Hook、注册文档事件并调度当前图纸；首次 NETLOAD 只完成部署并提示重启。
    /// </para>
    /// </summary>
    public void Initialize()
    {
        _unloaded = false;
        var initTimer = Stopwatch.StartNew();
        using var dialogSystemVariables = CadDialogSystemVariableScope.Capture();

        // Debug 构建自动开启 JSONL 诊断。
#if DEBUG
        DiagnosticLogger.Enable();
#endif
        DiagnosticLogger.Start("PluginEntry", "Initialize", "插件初始化开始");

        // 让 HandyControl 等嵌入依赖可被 .NET 加载。
        AppDomain.CurrentDomain.AssemblyResolve += OnResolveEmbeddedAssembly;
        DiagnosticLogger.Ok("PluginEntry", "RegisterEmbeddedAssemblyResolver", "嵌入程序集解析回调已注册");

        // 隐藏卸载入口必须在首次 NETLOAD、自动加载和部署加载场景下都可用。
        // 它不进入 CommandMethod/命令栈，只由 UnknownCommand 的完整名称匹配触发。
        RegisterHiddenUnloadCommand();
        DiagnosticLogger.Ok("PluginEntry", "RegisterHiddenUnloadCommand", "隐藏卸载入口已注册");

        // 平台服务必须最先注册，后续 Hook、窗口和日志都依赖它。
        DiagnosticLogger.Start("PluginEntry", "InitializePlatform", "开始初始化平台服务");
        PlatformManager.Initialize(CreatePlatform(), CreateFontHook(), CreateHost(), CreateLogger(), CreateFontScanner());
        DiagnosticLogger.Ok(
            "PluginEntry",
            "InitializePlatform",
            "平台服务初始化完成",
            new Dictionary<string, object?> { ["platform"] = PlatformManager.Platform.DisplayName });

        var log = LogService.Instance;
        try
        {
            EnsureFontAltDisabled(log);

            // 初始化自动加载注册表项；首次安装还会部署内嵌字体和默认配置。
            DiagnosticLogger.Start("PluginEntry", "AppInitializer", "开始执行注册表和默认字体初始化");
            bool isFirstRun = AppInitializer.Initialize();
            DiagnosticLogger.Ok(
                "PluginEntry",
                "AppInitializer",
                "注册表和默认字体初始化完成",
                new Dictionary<string, object?> { ["isFirstRun"] = isFirstRun });
            if (isFirstRun)
            {
                // 首次通过 NETLOAD 加载：CAD 已启动，Hook 无法拦截已加载的字体。
                // 仅完成注册表写入和字体部署，提示用户重启 CAD 后自动生效。
                try { AcadApp.SetSystemVariable("FONTMAP", ""); } catch { }
                DiagnosticLogger.Skip(
                    "PluginEntry",
                    "RuntimeStartup",
                    "首次加载跳过 Hook 安装、文档事件注册和替换调度",
                    new Dictionary<string, object?> { ["isFirstRun"] = true });
                log.Info("首次加载完成，默认替换字体已部署。请重启 CAD 使插件自动生效。");
                log.Flush();
                initTimer.Stop();
                DiagnosticLogger.Ok(
                    "PluginEntry",
                    "Initialize",
                    "插件首次加载初始化完成",
                    new Dictionary<string, object?> { ["isFirstRun"] = true },
                    initTimer.ElapsedMilliseconds);
                return;
            }

            // 非首次加载才进入运行链路：预热共享 TrueType 索引。
            DiagnosticLogger.Start("PluginEntry", "PrewarmSystemFonts", "开始预热系统 TrueType 字族索引");
            FontDetector.PrewarmSystemFonts();
            DiagnosticLogger.Ok("PluginEntry", "PrewarmSystemFonts", "系统 TrueType 字族索引预热已调度");

            // Hook 必须在图纸字体解析前安装，才可能捕获文件级加载请求。
            if (PlatformManager.Platform.SupportsNativeFontHooks)
            {
                DiagnosticLogger.Start("PluginEntry", "InstallFontHooks", "开始安装插件级持久字体 Hook");
                PlatformManager.FontHook.Install();
                DiagnosticLogger.Ok(
                    "PluginEntry",
                    "InstallFontHooks",
                    "插件级持久字体 Hook 安装流程完成",
                    new Dictionary<string, object?> { ["isInstalled"] = PlatformManager.FontHook.IsInstalled });
            }
            else
            {
                DiagnosticLogger.Skip(
                    "PluginEntry",
                    "InstallFontHooks",
                    "当前平台不支持 native 字体 Hook",
                    new Dictionary<string, object?> { ["platform"] = PlatformManager.Platform.DisplayName });
            }

            // 文档事件只负责调度，实际执行统一延迟到 Idle。
            var docMgr = AcadApp.DocumentManager;
            docMgr.DocumentCreated += OnDocumentCreated;
            docMgr.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            DiagnosticLogger.Ok("PluginEntry", "RegisterDocumentEvents", "文档事件已注册");

            // 当前活动图纸也走同一套 Idle 延迟执行。
            ScheduleExecution(null, "Startup");
            initTimer.Stop();
            DiagnosticLogger.Ok(
                "PluginEntry",
                "Initialize",
                "插件初始化完成",
                new Dictionary<string, object?> { ["isFirstRun"] = false },
                initTimer.ElapsedMilliseconds);
        }
        catch (System.Exception ex)
        {
            initTimer.Stop();
            DiagnosticLogger.Fail(
                "PluginEntry",
                "Initialize",
                "插件初始化失败",
                ex,
                durationMs: initTimer.ElapsedMilliseconds);
            log.Error("插件初始化失败", ex);
            log.Flush();
        }
    }

    /// <summary>AutoCAD 卸载插件时调用（通常在 CAD 退出时）。</summary>
    public void Terminate()
    {
        var timer = Stopwatch.StartNew();
        DiagnosticLogger.Start("PluginEntry", "Terminate", "插件正常卸载开始");
        try
        {
            PlatformManager.FontHook.Uninstall();
            DiagnosticLogger.Ok("PluginEntry", "UninstallFontHooks", "字体 Hook 卸载流程完成");
            UnregisterEvents();
            DiagnosticLogger.Ok("PluginEntry", "UnregisterEvents", "事件处理器已注销");
            AppDomain.CurrentDomain.AssemblyResolve -= OnResolveEmbeddedAssembly;
            ResolvedEmbeddedAssemblies.Clear();
            timer.Stop();
            DiagnosticLogger.Ok("PluginEntry", "Terminate", "插件正常卸载完成", durationMs: timer.ElapsedMilliseconds);
        }
        catch (System.Exception ex)
        {
            timer.Stop();
            DiagnosticLogger.Fail("PluginEntry", "Terminate", "插件正常卸载失败", ex, durationMs: timer.ElapsedMilliseconds);
        }
        finally
        {
            DiagnosticLogger.Disable();
        }
    }

    /// <summary>
    /// 卸载插件：注销所有事件、清空执行队列和文档跟踪。
    /// 由隐藏 AFRUNLOAD 入口调用。
    /// </summary>
    internal static void Unload()
    {
        var timer = Stopwatch.StartNew();
        DiagnosticLogger.Start("PluginEntry", "Unload", "隐藏卸载开始");
        lock (_scheduleLock)
        {
            _unloaded = true;
            _executeInProgress = false;
            UnregisterEvents();
            _pendingExecutions.Clear();
            if (_idleHandlerRegistered)
            {
                AcadApp.Idle -= OnDeferredIdle;
                _idleHandlerRegistered = false;
            }
        }
        DiagnosticLogger.Ok("PluginEntry", "ClearExecutionQueue", "延迟执行队列和事件状态已清理");

        try
        {
            AcadApp.SetSystemVariable("FONTALT", "simplex.shx");
            DiagnosticLogger.Ok("PluginEntry", "RestoreFontAlt", "FONTALT 已恢复为 simplex.shx");
        }
        catch (System.Exception ex)
        {
            DiagnosticLogger.Fail("PluginEntry", "RestoreFontAlt", "FONTALT 恢复失败", ex);
        }

        #if AFR_EXTERNAL_REGISTRY
        // 按所有权标记反向清理外部注册表值（默认禁用；旧版残留交由手动清理）。
        try { ExternalRegistryDefaultsApplier.Cleanup(); } catch { }
        #endif

        // 清理 FixedProfile.aws 中带 AFR 所有权标记的弹窗抑制节点。
        try
        {
            Diagnostics.AwsHideableDialogPatcher.Cleanup();
            DiagnosticLogger.Ok("PluginEntry", "CleanupAwsPatcher", "FixedProfile.aws AFR 节点清理完成");
        }
        catch (System.Exception ex)
        {
            DiagnosticLogger.Fail("PluginEntry", "CleanupAwsPatcher", "FixedProfile.aws AFR 节点清理失败", ex);
        }

        PlatformManager.FontHook.Uninstall();
        DiagnosticLogger.Ok("PluginEntry", "UninstallFontHooks", "字体 Hook 卸载流程完成");
        DocumentContextManager.Instance.Clear();
        DiagnosticLogger.Ok("PluginEntry", "ClearDocumentContext", "文档上下文已清理");

        // 注销嵌入程序集解析回调并释放缓存的嵌入程序集。
        // 否则 NETLOAD → AFRUNLOAD → NETLOAD 的反复加载会在 AppDomain.AssemblyResolve
        // 上累加多份回调；同时静态字段持有旧 HandyControl 实例的引用会阻止旧 DLL 卸载。
        AppDomain.CurrentDomain.AssemblyResolve -= OnResolveEmbeddedAssembly;
        ResolvedEmbeddedAssemblies.Clear();
        timer.Stop();
        DiagnosticLogger.Ok("PluginEntry", "Unload", "隐藏卸载完成", durationMs: timer.ElapsedMilliseconds);
        DiagnosticLogger.Disable();
    }

    private static void EnsureFontAltDisabled(LogService log)
    {
        DiagnosticLogger.Start("PluginEntry", "EnsureFontAltDisabled", "开始检查 FONTALT");
        try
        {
            object currentValue = AcadApp.GetSystemVariable(FontAltVariableName);
            string? current = currentValue?.ToString();
            if (IsFontAltDisabled(current))
            {
                DiagnosticLogger.Ok(
                    "PluginEntry",
                    "EnsureFontAltDisabled",
                    "FONTALT 已处于禁用状态",
                    new Dictionary<string, object?> { ["current"] = current ?? "<null>" });
                return;
            }

            string normalized = current!.Trim();
            AcadApp.SetSystemVariable(FontAltVariableName, DisabledFontAltValue);
            DiagnosticLogger.Ok(
                "PluginEntry",
                "EnsureFontAltDisabled",
                "FONTALT 已重设为禁用值",
                new Dictionary<string, object?>
                {
                    ["oldValue"] = normalized,
                    ["newValue"] = DisabledFontAltValue
                });
            log.Info($"FONTALT 已从 \"{normalized}\" 重设为 \"{DisabledFontAltValue}\"");
        }
        catch (System.Exception ex)
        {
            DiagnosticLogger.Fail("PluginEntry", "EnsureFontAltDisabled", "FONTALT 检测或重设失败", ex);
            log.Warning("FONTALT 检测或重设失败：" + ex.Message);
        }
    }

    private static bool IsFontAltDisabled(string? value)
    {
        if (value == null)
            return true;

        string trimmed = value.Trim();
        return trimmed.Length == 0
               || string.Equals(trimmed, DisabledFontAltValue, StringComparison.Ordinal);
    }

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

        UnregisterHiddenUnloadCommand();
    }

    /// <summary>
    /// 注册隐藏卸载入口，不把 AFRUNLOAD 加入 AutoCAD 命令栈。
    /// </summary>
    private static void RegisterHiddenUnloadCommand()
    {
        lock (_hiddenUnloadLock)
        {
            if (_hiddenUnloadRegistered) return;

            var docMgr = AcadApp.DocumentManager;
            docMgr.DocumentCreated += OnHiddenUnloadDocumentCreated;
            docMgr.DocumentToBeDestroyed += OnHiddenUnloadDocumentToBeDestroyed;

            foreach (Document doc in docMgr)
            {
                AttachHiddenUnloadHandler(doc);
            }

            _hiddenUnloadRegistered = true;
        }
    }

    /// <summary>从所有已跟踪文档注销隐藏卸载入口。</summary>
    private static void UnregisterHiddenUnloadCommand()
    {
        lock (_hiddenUnloadLock)
        {
            try
            {
                var docMgr = AcadApp.DocumentManager;
                docMgr.DocumentCreated -= OnHiddenUnloadDocumentCreated;
                docMgr.DocumentToBeDestroyed -= OnHiddenUnloadDocumentToBeDestroyed;
            }
            catch { }

            foreach (var doc in _hiddenUnloadDocuments.ToArray())
            {
                DetachHiddenUnloadHandler(doc);
            }

            _hiddenUnloadDocuments.Clear();
            _hiddenUnloadRegistered = false;
            _hiddenUnloadInProgress = false;
        }
    }

    /// <summary>给单个文档挂接 UnknownCommand 卸载入口。</summary>
    private static void AttachHiddenUnloadHandler(Document? doc)
    {
        if (doc == null || doc.IsDisposed) return;

        doc.UnknownCommand -= OnHiddenUnloadUnknownCommand;
        doc.UnknownCommand += OnHiddenUnloadUnknownCommand;
        _hiddenUnloadDocuments.Add(doc);
    }

    /// <summary>从单个文档移除 UnknownCommand 卸载入口。</summary>
    private static void DetachHiddenUnloadHandler(Document? doc)
    {
        if (doc == null) return;

        try { doc.UnknownCommand -= OnHiddenUnloadUnknownCommand; } catch { }
        _hiddenUnloadDocuments.Remove(doc);
    }

    private static void OnHiddenUnloadDocumentCreated(object sender, DocumentCollectionEventArgs e)
    {
        lock (_hiddenUnloadLock)
        {
            AttachHiddenUnloadHandler(e.Document);
        }
    }

    private static void OnHiddenUnloadDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
    {
        lock (_hiddenUnloadLock)
        {
            DetachHiddenUnloadHandler(e.Document);
        }
    }

    private static void OnHiddenUnloadUnknownCommand(object sender, UnknownCommandEventArgs e)
    {
        if (_unloaded) return;

        var commandName = e.GlobalCommandName?.Trim();
        if (!string.Equals(commandName, CommandNames.Unload, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_hiddenUnloadLock)
        {
            if (_hiddenUnloadInProgress) return;
            _hiddenUnloadInProgress = true;
        }

        AfrUnloadCommand.Execute();
    }

    /// <summary>
    /// 将一次字体处理请求加入 Idle 延迟队列。
    /// </summary>
    /// <param name="doc">目标文档（为 null 时在 Idle 处理时取活动文档）。</param>
    /// <param name="trigger">触发来源标识（如 "Startup"、"DocumentCreated"），用于日志。</param>
    private static void ScheduleExecution(Document? doc, string trigger)
    {
        lock (_scheduleLock)
        {
            if (_unloaded)
            {
                DiagnosticLogger.Skip(
                    "PluginEntry",
                    "ScheduleExecution",
                    "插件已卸载，跳过延迟执行调度",
                    new Dictionary<string, object?> { ["trigger"] = trigger });
                return;
            }

            _pendingExecutions.Enqueue((doc, trigger));
            if (!_idleHandlerRegistered)
            {
                _idleHandlerRegistered = true;
                AcadApp.Idle += OnDeferredIdle;
            }
            DiagnosticLogger.Ok(
                "PluginEntry",
                "ScheduleExecution",
                "已加入延迟执行队列",
                new Dictionary<string, object?>
                {
                    ["trigger"] = trigger,
                    ["pendingCount"] = _pendingExecutions.Count,
                    ["idleHandlerRegistered"] = _idleHandlerRegistered,
                    ["hasDocument"] = doc != null
                });
        }
    }

    /// <summary>
    /// AutoCAD 空闲时批量处理延迟队列；触发后立即注销自身。
    /// </summary>
    private static void OnDeferredIdle(object? sender, System.EventArgs e)
    {
        (Document? Doc, string Trigger)[] pending;
        lock (_scheduleLock)
        {
            // 重入防护：Regen() 抽消息泵时可能再次触发 Idle，
            // 若当前已在执行中则保留队列，待本次执行完成后的下一次 Idle 再处理。
            if (_executeInProgress)
            {
                DiagnosticLogger.Skip("PluginEntry", "OnDeferredIdle", "当前已有执行在进行，保留队列等待下一次 Idle");
                return;
            }

            AcadApp.Idle -= OnDeferredIdle;
            _idleHandlerRegistered = false;
            if (_unloaded || _pendingExecutions.Count == 0)
            {
                DiagnosticLogger.Skip(
                    "PluginEntry",
                    "OnDeferredIdle",
                    "插件已卸载或延迟执行队列为空",
                    new Dictionary<string, object?> { ["unloaded"] = _unloaded });
                return;
            }
            pending = [.. _pendingExecutions];
            _pendingExecutions.Clear();
            _executeInProgress = true;
        }

        DiagnosticLogger.Start(
            "PluginEntry",
            "OnDeferredIdle",
            "开始处理延迟执行队列",
            new Dictionary<string, object?> { ["pendingCount"] = pending.Length });
        try
        {
            for (int i = 0; i < pending.Length; i++)
            {
                var (doc, trigger) = pending[i];
                doc ??= AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null || doc.IsDisposed)
                {
                    DiagnosticLogger.Skip(
                        "PluginEntry",
                        "DeferredExecutionItem",
                        "延迟执行目标文档为空或已释放",
                        new Dictionary<string, object?>
                        {
                            ["trigger"] = trigger,
                            ["index"] = i
                        });
                    continue;
                }

                try
                {
                    DiagnosticLogger.Start(
                        "PluginEntry",
                        "DeferredExecutionItem",
                        "开始执行延迟文档任务",
                        new Dictionary<string, object?>
                        {
                            ["trigger"] = trigger,
                            ["index"] = i,
                            ["doc"] = DocumentContextManager.ReadDocumentName(doc)
                        });
                    ExecutionController.Execute(doc, trigger);
                    DiagnosticLogger.Ok(
                        "PluginEntry",
                        "DeferredExecutionItem",
                        "延迟文档任务执行完成",
                        new Dictionary<string, object?>
                        {
                            ["trigger"] = trigger,
                            ["index"] = i,
                            ["doc"] = DocumentContextManager.ReadDocumentName(doc)
                        });
                }
                catch (System.Exception ex)
                {
                    DiagnosticLogger.Fail(
                        "PluginEntry",
                        "DeferredExecutionItem",
                        "延迟文档任务执行失败",
                        ex,
                        new Dictionary<string, object?>
                        {
                            ["trigger"] = trigger,
                            ["index"] = i
                        });
                }
            }
            DiagnosticLogger.Ok(
                "PluginEntry",
                "OnDeferredIdle",
                "延迟执行队列处理完成",
                new Dictionary<string, object?> { ["pendingCount"] = pending.Length });
        }
        finally
        {
            lock (_scheduleLock)
            {
                _executeInProgress = false;
                // 若执行期间有新文档入队（Regen 消息泵触发的 DocumentCreated），
                // 此处重新注册 Idle，确保下一轮空闲时处理这些请求。
                if (!_unloaded && _pendingExecutions.Count > 0 && !_idleHandlerRegistered)
                {
                    _idleHandlerRegistered = true;
                    AcadApp.Idle += OnDeferredIdle;
                }
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
