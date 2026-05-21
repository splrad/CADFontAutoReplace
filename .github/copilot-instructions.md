# CADFontAutoReplace 仓库记忆

本文档是本仓库的长期协作记忆，应跟随代码进入 GitHub。代码实现变化时，优先同步本文档、README 与开发者指南。

## 当前项目事实

- 项目名：`CADFontAutoReplace`，简称 AFR。
- 主用途：为 AutoCAD 图纸自动处理缺失字体、样式表字体替换、样式表 `@TrueType` 字体运行时映射、`LdFileHook` 字体加载重定向，以及 MText 内联字体映射。
- 项目不再处理任何单行文字编码修复、AI 决策、native decode evidence、训练数据、候选包、模型或补绘问题。
- 支持 AutoCAD 版本：2018 到 2027。
- 主要分发方式：`AFR.Deployer` 部署工具一键安装/卸载。
- 次要分发方式：单 DLL `NETLOAD`，用于维护、测试和受限环境。
- 统一版本来源：根目录 `Version.props`。

## 架构规则

依赖方向必须保持：

```text
AFR.Core -> AFR.UI -> AFR.AutoCAD -> AFR-ACAD20XX
```

- `AFR.Core` 与 `AFR.UI` 禁止引用 AutoCAD SDK。
- `AFR.AutoCAD` 不依赖具体版本壳。
- 版本壳 `AFR-ACAD20XX` 只负责平台常量、插件入口和项目打包。
- 跨层能力通过 `PlatformManager`、共享项目和明确的服务边界协调，不引入无必要的 DI 或抽象层。
- 修改 Hook、注册表、部署器安装/卸载路径时必须保持小范围变更，并同步文档。

## 当前命令

Release 命令：

- `AFR`：字体配置。
- `AFRLOG`：字体替换日志与手动样式调整界面。
- `AFRUNLOAD`：隐藏维护入口，不通过 `CommandMethod` 注册；只在完整输入时由 `UnknownCommand` 路由触发。

Debug 命令：

- `AFRVIEW`：查看 MText / MLeader 格式与样式诊断。
- 其他 Debug 辅助命令必须用 `#if DEBUG` 控制，并在版本壳注册处同步控制。

禁止恢复已删除的单行文字编码修复、训练、样本导入、模型查看、模型替换、导出训练包或补绘命令。

## 字体替换链路

主链路在 `ExecutionController.Execute`：

1. 使用 `FontDetector` 检测样式表缺失字体，并由 `FontReplacer.ReplaceMissingFonts()` 对普通缺失字体和 `@SHX` 缺失字体执行永久替换。
2. 替换后重新检测并存储仍缺失结果，供 `AFRLOG` 标记当前状态。
3. 使用 `StyleTextStyleHook` 只对样式表 `@TrueType` 缺失字体执行临时运行时映射。
4. 扫描 MText 内联字体；`MTextInlineFontHook` 只在 MText 作用域内处理全部内联缺失字体，并直接记录实际映射结果。
5. 需要时执行最终视觉刷新。

当前 Hook 职责边界：

- `NativeInlineHook`、`NativeHookTarget`、`NativeFontHookProfile`、`INativeFontHookExportsProvider` 是字体 Hook 共享基础设施，必须保留。
- `LdFileHook` 只处理字体加载阶段的透明重定向；所有进入它的字体必须由上层 Hook 在触发原生加载前调用 `TryPreRegisterRuntimeBridge` 预登记。
- `StyleTextStyleHook` 的正式样式表运行时映射只负责 `@TrueType` 缺失字体；它在 `loadStyleRec` 前观察到 `@SHX` 时只允许做 `LdFileHook` 预登记防线，不记录样式表运行时映射、不改写样式。
- `MTextInlineFontHook` 只负责 MText 内联缺失字体运行时映射。
- MText Hook 不能处理样式表字体；样式表 Hook 不能处理 MText 内联字体。
- 普通样式表检测、永久替换和二次验证必须优先使用当前 `Database` 上的 CAD 托管 API。
- `FontAvailabilityIndex` 只是 Hook 侧无法安全取得托管 `Database` 时的进程级兜底索引。

样式表处理规则：

- 样式表非 `@` 缺失字体必须走永久替换。
- 样式表 `@SHX` 主字体和 `@SHX` 大字体缺失也必须走永久替换。
- 样式表 `@TrueType` 不写回；去 `@` 后基础字体存在则放行，不存在才通过 `StyleTextStyleHook -> LdFileHook` 映射到配置 TrueType。

MText 内联运行时映射规则：

- MText 内联缺失字体不改写 `MText.Contents`。
- MText 内联非 `@` TrueType / SHX 主字体 / SHX 大字体由 `MTextInlineFontHook` 直接映射当前显示参数。
- MText 内联 `@TrueType` / `@SHX` 主字体 / `@SHX` 大字体才登记给 `LdFileHook` 做加载桥接。
- AFRLOG 展示记录必须来自 `MTextInlineFontHook` 实际命中的业务映射结果。

典型回归：`e5f3b311` 收窄样式表 Hook 职责时移除了 `loadStyleRec` 前置 `@SHX` 预登记，导致 MText 内联 `@gbcbig.shx` 首次展开早于 `LdFileHook` 登记并出现乱码；`c9fc7199` 通过恢复 `loadStyleRec` trampoline 前的 SHX 加载预注册修复。今后重构 Style/MText/LdFile 边界时，不能删除任何“进入 `LdFileHook` 前先登记”的防线。

## 发布资产

发布资产统一由 `tools/Publish-ReleaseAssets.ps1` 生成。

脚本职责：

1. 自动发现 `src/AutoCAD/AFR-ACAD*/AFR-ACAD*.csproj`。
2. Release 构建所有版本壳。
3. 校验 `artifacts/bin/AFR-ACAD*/release/` 下的 DLL 与 `.cad.json`。
4. 发布 `AFR.Deployer` 自包含单文件 EXE。
5. 生成 GitHub Release 上传资产。

输出约定：

```text
publish/AFR.Deployer/AFR-Deployer.exe
artifacts/ReleaseAssets/AFR-Deployer_vX.Y.Z.exe
artifacts/ReleaseAssets/AFR-DLL_vX.Y.Z.zip
artifacts/ReleaseAssets/Fonts.zip
```

## 文档维护规则

- README 面向使用者，默认推荐部署器安装；单 DLL/NETLOAD 只作为补充路径。
- 开发者指南说明构建、调试、命令注册、发布资产和字体 Hook 边界。
- Debug 调查文档必须只保留当前代码仍存在的命令、类和流程。
- 本地临时日志、构建产物、浏览器 profile、截图、反汇编结果不要进入 GitHub。
- `.github/copilot-instructions.md` 是真正的仓库记忆文件，不要加入 `.gitignore`。

## 提交前检查

- `dotnet build CADFontAutoReplace.slnx` 能通过，或明确说明本机缺失 SDK/AutoCAD 依赖。
- 发布相关变更应验证 `tools/Publish-ReleaseAssets.ps1`。
- Hook 变更应验证 `LdFileHook`、`StyleTextStyleHook`、`MTextInlineFontHook` 的触发边界。
- 新增文档必须能从 README 或开发者指南找到入口，除非它明确是本地临时调查文件。
