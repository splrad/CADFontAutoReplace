# CADFontAutoReplace 仓库记忆

本文档是本仓库的长期协作记忆，应跟随代码进入 GitHub。代码实现变化时，优先同步本文档、`README.md`、`PROJECT_HANDOVER.md` 与开发者指南。若文档与源码冲突，以当前源码为准，并立即修正文档。

## 当前项目事实

- 项目名：`CADFontAutoReplace`，简称 AFR。
- 当前范围：AutoCAD 缺失字体自动替换、样式表字体替换、样式表 `@TrueType` 运行时映射、`LdFileHook` 字体加载桥接、MText 内联字体运行时映射，以及 `AFR.Deployer` 一键安装/卸载。
- 当前源码树不包含 `AFR.GlyphCore`、WenShu、DBText 修复、AI 决策、native decode evidence、训练数据、候选包、模型、报告或补绘链路。旧记忆或旧文档中的这些名称均视为历史上下文，除非源码重新引入。
- 支持 AutoCAD 2018 到 2027。版本壳目标框架为：2018=`net462`，2019/2020=`net472`，2021-2024=`net48`，2025/2026=`net8.0-windows`，2027=`net10.0-windows`。
- 主要分发方式：`AFR.Deployer` 部署工具一键安装/卸载。
- 次要分发方式：单 DLL `NETLOAD`，用于维护、测试和受限环境。
- 统一版本来源：根目录 `Version.props`。发版时只改 `PluginDisplayVersion` 和必要的 `PluginBuildId`。
- .NET SDK 选择由 `global.json` 控制；当前为 `10.0.201`，`rollForward=latestFeature`。
- `Directory.Packages.props` 已关闭 CPM；包版本在各项目自己的 `.csproj` 中维护。

## 代码结构与依赖

当前源码结构：

```text
src/AFR.Core              基础接口、命令常量、模型、配置、日志和共享服务
src/AFR.UI                插件侧 WPF 窗口与 ViewModel
src/AFR.HostIntegration   部署器与插件共用的字体释放、AWS 弹窗抑制基础能力
src/AFR.Polyfills         仅面向 .NET 5 以下版本壳的兼容补丁
src/AutoCAD/AFR.AutoCAD   AutoCAD 命令、字体检测替换、Hook、执行编排
src/AutoCAD/AFR-ACAD20XX  各 AutoCAD 版本壳工程
src/AFR.Deployer          WPF 部署器
tools                     发布脚本
docs                      使用与开发文档
```

依赖方向与边界：

- AutoCAD 插件 DLL 由版本壳导入共享项目组成：`AFR.Core`、`AFR.UI`、`AFR.AutoCAD`、`AFR.HostIntegration`，旧框架版本再导入 `AFR.Polyfills`。
- `AFR.Core`、`AFR.UI`、`AFR.HostIntegration`、`AFR.Polyfills` 禁止引用 AutoCAD SDK。
- `AFR.AutoCAD` 才能持有 AutoCAD 托管 API 类型、命令、Hook 和执行流程。
- 版本壳 `AFR-ACAD20XX` 只负责目标框架、AutoCAD 包版本、平台常量、`PluginEntry`、`CommandClass` 和发布元数据。
- `AFR.Deployer` 是独立 `net10.0-windows` WPF 应用，只导入 `AFR.HostIntegration`，不引用 AutoCAD SDK，也不直接引用插件项目。
- `src/AutoCAD/Directory.Build.targets` 会把 `HandyControl` 嵌入插件 DLL，并在 Release 构建后生成 `AFR-ACAD20XX.cad.json` sidecar。
- 跨层能力通过 `PlatformManager`、共享项目和明确服务边界协调，不引入无必要的 DI 或抽象层。
- 修改 Hook、注册表、部署器安装/卸载路径时必须保持小范围变更，并同步文档。

## 插件生命周期

`PluginEntryBase.Initialize` 的当前顺序：

1. Debug 构建启用 `DiagnosticLogger`，输出 `AFR_Diag_*.jsonl`，每行一个结构化时序事件。
2. 注册嵌入程序集解析回调，只为插件 DLL 内嵌的 `HandyControl` 服务。
3. 注册隐藏 `AFRUNLOAD` 路由；该路由必须在首次 `NETLOAD`、自动加载和部署加载场景下都可用。
4. 初始化 `PlatformManager`。
5. 将 `FONTALT` 设为 `.`，避免 AutoCAD 默认替代字体干扰 AFR 的判断。
6. 执行 `AppInitializer.Initialize()`，初始化注册表默认值并释放内嵌默认字体。
7. 首次加载时只完成注册表和字体部署，清空 `FONTMAP`，提示用户重启；此时不安装 Hook、不注册文档事件、不调度替换。
8. 非首次加载时预热系统 TrueType 字族索引，安装全局字体 Hook，注册文档创建/销毁事件，并通过 Idle 队列延迟执行当前文档。

卸载边界：

- `Terminate()` 用于 AutoCAD 正常卸载：卸载 Hook、关闭诊断日志、注销事件、解除嵌入程序集解析。
- `AFRUNLOAD` 只由 `UnknownCommand` 精确匹配触发；执行时注销事件、卸载 Hook、清空执行队列和文档上下文、清理 AFR 自动加载注册表项，并清理由插件写入的 `FixedProfile.aws` 节点。
- `AFRUNLOAD` 会把 `FONTALT` 尝试恢复为 `simplex.shx`。

## Debug 诊断日志

- Debug 诊断文件为插件目录下的 `AFR_Diag_*.jsonl` JSONL 事件流，不再输出旧文本格式。
- 每行 JSON 事件包含 `seq`、`timestamp`、`level`、`status`、`module`、`operation`、`message`、`threadId`、`context`、`durationMs` 和 `error`。
- `status` 固定使用 `START`、`OK`、`FAIL`、`SKIP`；排查问题时先按 `seq` 还原插件时序，再过滤 `FAIL` / `SKIP` 定位失败或跳过分支。
- 新增诊断只能使用 `Start`、`Ok`、`Fail`、`Skip`、`RunStep` 或现有结构化领域方法；旧文本日志兼容入口已移除。
- 字体映射是否真正生效仍以文件级 Hook 的真实命中记录和最终 `redirects` 计数为准，不能只看预登记事件。

## 当前命令

Release 命令：

- `AFR`：打开字体配置窗口，保存全局替换字体；非首次加载且 Hook 已安装时会立即处理当前图纸。
- `AFRLOG`：打开字体替换日志和手动样式调整界面。
- `AFRUNLOAD`：隐藏维护入口，不通过 `CommandMethod`/`CommandClass` 注册；只在完整输入时由 `UnknownCommand` 路由触发。

Debug 命令：

- 当前真实注册的 Debug 命令只有 `AFRVIEW`，用于查看 MText / MLeader 格式与样式诊断。
- `AFRINSERT`、`AFRDUMPPROFILE`、`AFRSHOWAWSPATH`、`AFRGENPROBESCRIPTS`、`AFRDUMPDIALOGAPI` 等旧调试入口在当前源码中未作为可用命令注册；不要在文档中描述为可执行命令，除非同时恢复 `CommandNames`、`CommandMethod` 和 `CommandClass`。
- 其他 Debug 辅助命令必须用 `#if DEBUG` 或项目条件控制，并在命令注册处同步控制。

新增命令规则：

- 新增命令名先登记到 `src/AFR.Core/Constants/CommandNames.cs`。
- 若命令位于已有 `AfrCommands` 类中，版本壳现有 `[assembly: CommandClass(typeof(AFR.Commands.AfrCommands))]` 已覆盖。
- 若新增命令类，必须在所有版本壳 `PluginEntry.cs` 中补充 `CommandClass`，或放入带 assembly 级 `CommandClass` 的共享源码并确认只在目标配置编译。
- 隐藏维护入口不要通过 `CommandMethod` 注册，避免进入 CAD 命令补全体系。

禁止恢复已删除的单行文字编码修复、训练、样本导入、模型查看、模型替换、导出训练包、报告或补绘命令。

## 字体替换链路

主链路在 `ExecutionController.Execute`：

1. `AutoCadFontHook.Install()` 在插件启动时持久安装 `LdFileHook`、`ShpLoadHook`、`StyleTextStyleHook` 和 `MTextInlineFontHook`；文档处理只清理运行时登记、样式映射、MText 候选和诊断计数。
2. 使用 `FontDetector.DetectMissingFonts()` 检测样式表缺失字体，并把原始检测结果存入 `DocumentContextManager` 供 `AFRLOG` 使用。
3. 使用 `FontDetector.CollectRuntimeFontMappings()` 只收集样式表 `@TrueType` 缺失字体的临时映射，随后通过 `StyleTextStyleHook.EnterStyleRuntimeOperation()` 主动触发 `LoadStyleRec`，并立即 `Regen` 让文件级 Hook 命中。
4. 样式表运行时映射结果只接受 `FontRuntimeMappingStore.GetRuntimeMappingResults()` 中的实际 Hook 命中记录。
5. 只读扫描 MText 内联字体，把候选交给 `MTextInlineFontHook.ReplaceInlineFontCandidates()` 并预登记运行时请求；只有产生文件级登记时才触发 MText `Regen`。
6. MText 内联映射结果同样只接受 `FontRuntimeMappingStore.GetRuntimeMappingResults()` 中的实际文件级 Hook 命中记录。
7. 最后使用 `FontReplacer.ReplaceMissingFonts()` 对普通缺失字体和 `@SHX` 缺失字体执行样式表永久替换；替换前必须校验替换字体可用性。
8. 替换后重新检测并存储仍缺失结果，供 `AFRLOG` 标记当前状态。
9. 清理文档级运行时登记和候选后，如果发生样式表永久替换，通过 `MarkAffectedTextGraphicsModified()` 标记受影响文字、属性和块引用，再做最终视觉刷新。
10. 写入统计、诊断汇总和 `DocumentContextManager.MarkExecuted(doc)`，避免同一文档重复执行。

该流程不包含任何单行文字修复、AI 推理、训练或补绘阶段。

## 字体检测与替换规则

样式表处理规则：

- `ShapeFile` 样式用于复杂线型，检测和替换都必须跳过。
- 样式表非 `@` 缺失字体必须走永久替换。
- 样式表 `@SHX` 主字体和 `@SHX` 大字体缺失也必须走永久替换。
- 样式表 `@TrueType` 不写回；只有 Windows GDI `EnumFontFamiliesExW` 实际枚举到同名 `@face` 才放行，否则通过 `StyleTextStyleHook -> ShpLoadHook` 映射到配置 TrueType。
- TrueType face 可用性以 Windows GDI 枚举为准，不能用“基础字体存在”推断 `@TrueType` 存在；SHX 可用性使用 `HostApplicationServices.Current.FindFile` 和 SHX 文件头分类。
- 替换 TrueType 时必须先清空 `BigFontFileName`、`FileName`，再写入 `FontDescriptor`；替换 SHX 时必须清空残留 `FontDescriptor`。

MText 内联运行时映射规则：

- MText 内联字体不改写 `MText.Contents`。
- `MTextInlineFontHook` 随插件持久安装；`MTextInlineFontScanner` 只读扫描 MText 内容，再把候选交给 `MTextInlineFontHook.ReplaceInlineFontCandidates()`；只有预登记出文件级请求后才触发 Regen。
- `MTextInlineFontHook` 只作为 MText 内联字体来源识别域，负责把缺失请求登记到统一运行时登记表，不做 setter/构造函数直接替换。
- MText 内联 SHX 主字体 / SHX 大字体只登记给 `LdFileHook`；MText 内联 TrueType 只登记给 `ShpLoadHook`。
- MText 内联 `@SHX` 先尝试去 `@` 后基础 SHX，基础存在则映射基础 SHX，否则映射配置 SHX；`@TrueType` 必须由 GDI 精确判断同名 `@face` 是否存在。
- AFRLOG 展示记录必须来自 `LdFileHook` / `ShpLoadHook` 实际命中的文件级映射结果，不在界面层重新推导候选映射。

## Hook 职责边界

必须保留的共享字体 Hook 基础设施：

- `NativeInlineHook`
- `NativeHookTarget`
- `NativeFontHookProfile`
- `INativeFontHookExportsProvider`

当前 Hook 边界：

- `AutoCadFontHook.Install()` 持久安装 `LdFileHook`、`ShpLoadHook`、`StyleTextStyleHook`、`MTextInlineFontHook` 和 Debug `MapFontDiagnosticHook`，并只初始化 `FontAvailabilityIndex`；GDI TrueType face 索引必须按需构建，不能放进 CAD 启动同步热路径。
- `ExecutionController.Execute()` 不安装或卸载来源 Hook，只在每个文档周期开始和结束清理 `FontRuntimeRequestRegistry`、样式表运行时映射、MText 候选和诊断计数；最终视觉刷新必须发生在文档级登记清理之后。
- `FontRuntimeRequestRegistry` 是来源 Hook 与文件级 Hook 之间唯一的运行时请求登记表，登记项必须包含来源、字体类型、原始字体、基础字体、目标字体和执行 Hook。
- `LdFileHook` 是唯一 SHX 文件级映射执行点；它只处理已登记 SHX 请求，未登记请求立即放行。
- `ShpLoadHook` 是唯一 TrueType 文件级映射执行点；它只处理已登记 TrueType 请求，未登记请求立即放行。
- `StyleTextStyleHook` 只负责样式表来源的缺失 `@TrueType` 登记，不处理样式表 `@SHX`，不处理 MText 内联字体。
- `StyleTextStyleHook` 可能走 `loadStyleRec` thunk Hook 或普通 inline Hook；修改导出符号、RVA、前缀校验或 trampoline 逻辑时必须逐版本验证。
- `MTextInlineFontHook` 只负责 MText 内联缺失字体登记，不处理样式表字体，不直接替换 CAD 当前显示参数。
- MText Hook 不能处理样式表字体；样式表 Hook 不能处理 MText 内联字体。
- 普通样式表检测、永久替换和二次验证必须优先使用当前 `Database` 上的 CAD 托管 API。
- `FontAvailabilityIndex` 只是 Hook 侧无法安全取得托管 `Database` 时的进程级 CAD 字体兜底索引；系统 TrueType face，尤其 `@TrueType` vertical face，由 `GdiTrueTypeFontFaceIndex` 按字体名精确查询并缓存。

典型回归：把来源识别和文件级执行混在同一个 Hook 中，会导致未登记字体被全局兜底、`@TrueType` 被错误地按基础字体判断，或 MText setter 直接替换绕过文件级 Hook。今后重构 Style/MText/LdFile/ShpLoad 边界时，必须保持“来源限定、登记驱动、未登记快速放行”。

## AFRLOG 与文档上下文

- `DocumentContextManager` 存储每个文档的原始缺失字体检测结果、替换后仍缺失结果、MText 内联映射结果和样式表运行时映射结果。
- `AFRLOG` 每次打开都会重新检测当前文档，反映 `STYLE`/`ST` 命令或手动修改后的最新状态。
- 有原始检测结果时，`AFRLOG` 以原始结果为主列表，用当前检测结果标记仍缺失样式，避免已替换样式在日志中消失。
- `AFRLOG` 手动替换只调用 `FontReplacer.ReplaceByStyleMapping()` 写入当前图纸样式表，不修改注册表全局配置。
- `AFR` 修改全局配置后，若文档已有历史检测结果，会复用原始检测结果按新配置重新覆盖样式，避免因旧替换字体已可用而误判“不缺失”。

## 部署器与发布资产

`AFR.Deployer` 当前事实：

- 目标框架为 `net10.0-windows`，`win-x64`，自包含单文件发布。
- `app.manifest` 请求 `requireAdministrator`，安装/卸载时应预期 UAC。
- 不需要 Windows App Runtime 作为外置依赖；不要重新引入该要求，除非代码确实改为依赖 WinAppSDK。
- 通过 `AFR.HostIntegration` 共用内嵌 SHX 字体释放与 `FixedProfile.aws` 弹窗抑制基础逻辑。
- 插件 DLL 与 `.cad.json` 从 `artifacts/bin/AFR-ACAD*/release/` 嵌入部署器资源；新增 AutoCAD 版本时优先让发布脚本生成标准构建输出，不手工复制到部署器资源目录。

发布资产统一由 `tools/Publish-ReleaseAssets.ps1` 生成。

脚本职责：

1. 自动发现 `src/AutoCAD/AFR-ACAD*/AFR-ACAD*.csproj`。
2. Release 构建所有版本壳。
3. 校验 `artifacts/bin/AFR-ACAD*/release/` 下的 DLL 与 `.cad.json`。
4. 发布 `AFR.Deployer` 自包含单文件 EXE。
5. 从 `chore/Fonts.zip` 复制字体包。
6. 生成 GitHub Release 上传资产。

输出约定：

```text
publish/AFR.Deployer/AFR-Deployer.exe
artifacts/ReleaseAssets/AFR-Deployer_vX.Y.Z.exe
artifacts/ReleaseAssets/AFR-DLL_vX.Y.Z.zip
artifacts/ReleaseAssets/Fonts.zip
```

发布脚本不接受模型、模型清单、训练包或原生推理运行时参数。

## 文档维护规则

- README 面向使用者，默认推荐部署器安装；单 DLL/NETLOAD 只作为补充路径。
- `PROJECT_HANDOVER.md` 是当前维护范围、执行流程和验证清单的短版交接文档。
- 开发者指南说明构建、调试、命令注册、发布资产和字体 Hook 边界。
- Debug 调查文档必须只保留当前代码仍存在的命令、类和流程。
- 本地临时日志、构建产物、浏览器 profile、截图、反汇编结果不要进入 GitHub。
- `.github/copilot-instructions.md` 是真正的仓库记忆文件，不要加入 `.gitignore`。
- 若以后重新引入任何 DBText/GlyphCore/AI/训练链路，必须先在代码、数据边界、发布资产、隐私规则和文档中重新定义，不要复用历史记忆中的旧路径。

## 提交前检查

- 常规变更：`dotnet build CADFontAutoReplace.slnx -c Debug` 与 `dotnet build CADFontAutoReplace.slnx -c Release` 能通过，或明确说明本机缺失 SDK/AutoCAD 依赖。
- 发布相关变更应验证 `tools/Publish-ReleaseAssets.ps1`。
- Hook 变更应验证 `LdFileHook`、`StyleTextStyleHook`、`MTextInlineFontHook` 的触发边界。
- 命令变更应验证 `CommandNames.cs`、`CommandMethod`、`CommandClass` 和 Debug/Release 暴露范围。
- 部署器变更应验证 UAC、注册表扫描、安装/卸载、内嵌插件资源和 `.cad.json` 解析。
- 文档变更至少运行 `git diff --check`，确保没有空白错误。
- 新增文档必须能从 README 或开发者指南找到入口，除非它明确是本地临时调查文件。
