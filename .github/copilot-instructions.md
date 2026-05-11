# CADFontAutoReplace 仓库记忆

本文档是本仓库的长期协作记忆。它应跟随代码进入 GitHub，用于约束后续 AI/Copilot/Codex 修改方向。若代码实现变化，优先更新本文档和相关公开文档，再删除过期调查文档。

## 当前项目事实

- 项目名：`CADFontAutoReplace`，简称 AFR。
- 主用途：为 AutoCAD 图纸自动处理缺失字体、MText 内联字体引用，以及受人工标签约束的 DBText 单行文字修复。
- 支持 AutoCAD 版本：2018 到 2027。
- 主要分发方式：`AFR.Deployer` 部署工具一键安装/卸载。
- 次要分发方式：单 DLL `NETLOAD`，用于维护、测试和受限环境。
- 统一版本来源：根目录 `Version.props`。
- 公开说明入口：`README.md`、`docs/developer-guide-beginner.md`、`docs/developer-guide-advanced.md`。

## 架构规则

依赖方向必须保持：

```text
AFR.Core -> AFR.UI -> AFR.AutoCAD -> AFR-ACAD20XX
```

- `AFR.Core` 与 `AFR.UI` 禁止引用 AutoCAD SDK。
- `AFR.AutoCAD` 不依赖具体版本壳。
- 版本壳 `AFR-ACAD20XX` 只负责平台常量、插件入口和项目打包。
- 跨层能力通过 `PlatformManager`、共享项目和明确的服务边界协调，不引入无必要的 DI 或抽象层。
- 修改 Hook、注册表、部署器安装/卸载、模型写入路径时必须保持小范围变更，并同步文档。

## 当前命令

Release 命令：

- `AFR`：字体配置。
- `AFRLOG`：字体替换日志与手动样式调整界面。
- `AFRDBTEXTLABEL`：选择 DBText 单行文字并进行人工确认。
- `AFRUNLOAD`：隐藏维护入口，不通过 `CommandMethod` 注册；只在完整输入时由 `UnknownCommand` 路由触发。

Debug 命令：

- `AFRDBTEXTMODEL`：查看 DBText 修复模型状态与神经参数评估。
- `AFRINSPECTTEXT`：检查单个文字对象、DBText 模型候选、AI 分数和自动决策；这是当前模型链路的托管诊断命令，不依赖旧 native hook。
- `AFRDBTEXTBATCHTRAIN`：批量生成保守 DBText 训练标签，仅 Debug 构建可用。
- `AFRVIEW`：查看 MText / MLeader 格式与样式诊断。
- `AFRINSERT`：插入测试 MText。
- `AFRDUMPPROFILE`、`AFRSHOWAWSPATH`、`AFRGENPROBESCRIPTS`、`AFRDUMPDIALOGAPI`：调试/反射辅助命令。

新增命令时：

- 先在 `src/AFR.Core/Constants/CommandNames.cs` 登记。
- 常规命令需要在版本壳 `PluginEntry.cs` 注册 `CommandClass`。
- Debug-only 命令必须在命令文件和注册处同时用 `#if DEBUG` 控制。
- `AFRUNLOAD` 这类隐藏入口不要注册进 CAD 命令补全体系。

## 字体替换链路

主链路在 `ExecutionController.Execute`：

1. 使用 `FontDetector` 检测样式表缺失字体。
2. 使用 `FontReplacer` 替换 SHX 主字体、SHX 大字体与 TrueType。
3. 执行 DBText 模型修复。
4. 扫描 MText 内联字体。
5. 用 `LdFileHook` 重定向记录与 MText 正向扫描结果交叉生成修复记录。
6. 输出摘要并刷新显示。

注意事项：

- `AFRLOG` 不是通用 DiagnosticLogger 查看器；它主要展示字体表替换和内联字体替换的 UI。
- TrueType 样式表替换必须继续使用 TrueType，避免污染 AutoCAD 字体缓存。
- MText 内联缺失 TrueType 可转换为 SHX `\F` 格式，让后续渲染走 SHX/Hook 路径。
- SHX 字形缺笔、字体形状不正确，与 DBText 编码修复是两类问题，不要混在同一修复策略里。

## DBText 模型修复

当前代码不再使用旧的原生 code page Hook 探针作为公开 DBText 修复路径。

当前实现：

- 自动修复入口：`src/AutoCAD/AFR.AutoCAD/Services/DbTextRepair/DbTextRepairService.cs`。
- 人工确认入口：`src/AutoCAD/AFR.AutoCAD/Commands/DbTextManualLabelCommand.cs`。
- 模型存储：`src/AutoCAD/AFR.AutoCAD/Services/DbTextRepair/DbTextRepairModelStore.cs`。
- 候选生成：`DbTextRepairCandidateGenerator`。
- 自动决策：`DbTextRepairAdvisor` + `DbTextRepairPolicy`。
- 模型结构：`src/AFR.Core/DbTextRepairModel/`。
- 内置数据集：`data/DbTextRepairModel.jsonl`。

数据规则：

- 插件和部署器都嵌入 `data/DbTextRepairModel.jsonl`，资源名为 `AFR.DbTextRepairModel.jsonl`。
- 本地开发时，若运行目录可向上找到仓库根目录，活动模型目录优先使用仓库 `data/`。
- 正式安装时，部署器把内置数据集合并到 `%APPDATA%\CADFontAutoReplace\DbTextRepairModel.jsonl`。
- `DbTextRepairModel*.jsonl` 导入文件合并成功后会删除；损坏文件会重命名为 `.corrupt.*.jsonl`。

自动写回规则：

- 候选可以来自当前文本、`big5-carrier-to-gbk` 重新解释、历史人工标签。
- 自动写回必须受精确标签约束；神经模型只用于评分和排序，不能单独作为写回证据。
- `keep`、`glyph-issue`、冲突标签、无精确标签时不自动修改图纸。
- 不要把 `big5-carrier-to-gbk` 候选扩展为全局字典替换。

人工确认规则：

- `AFRDBTEXTLABEL` 选择单个 DBText。
- 用户可选择写回正确文本、保持当前文本、标记字体/字形问题。
- 写回或标签记录是对象级显式决策，不应自动泛化为整图或全局规则。

## 已删除的旧 DBText 调查内容

以下旧文档/概念不代表当前代码，不应恢复为公开说明：

- `docs/debugging/README-DBText-Investigation.md`
- `docs/debugging/IMPLEMENTATION-PLAN.md`
- `docs/debugging/USAGE-EXAMPLE.md`
- `docs/debugging/DBText-CodePage-Investigation.md`
- `docs/debugging/DBText-Investigation-Guide.txt`
- `docs/Big5HookInvestigationMemory.md`
- `AFRDBTEXTPROBE`
- `AFRTRACERSTART` / `AFRTRACERREPORT` / `AFRTRACERSTOP`
- `DwgFilerCodePageScopeHook`
- `TextEditorDbcsDecodeHook`
- `CodePageFamilyHook`

若未来重新调查 native code page 链路，必须作为新的 Debug-only 实验重新建立，不能把旧结论当作当前 Release 行为。

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

注意：

- `artifacts/ReleaseAssets` 使用大小写清晰的目录名。
- `Fonts.zip` 来源是 `chore/Fonts.zip`。
- GitHub Release 工作流只负责调用脚本和上传脚本产物，不要在 YAML 中复制一份打包逻辑。

## 文档维护规则

- README 面向使用者，默认推荐部署器安装；单 DLL/NETLOAD 只作为补充路径。
- 开发者指南说明构建、调试、命令注册、发布资产和 DBText 模型维护。
- Debug 调查文档必须只保留当前代码仍存在的命令、类和流程。
- 本地临时日志、构建产物、截图、反汇编结果不要进入 GitHub。
- `.github/copilot-instructions.md` 是真正的仓库记忆文件，不要再加入 `.gitignore`。

## 提交前检查

- `dotnet build CADFontAutoReplace.slnx` 能通过，或明确说明本机缺失 SDK/AutoCAD 依赖。
- 发布相关变更应验证 `tools/Publish-ReleaseAssets.ps1`。
- DBText 模型变更应验证 `AFRDBTEXTLABEL`、模型合并路径、嵌入资源名和相关文档。
- 新增文档必须能从 README 或开发者指南找到入口，除非它明确是本地临时调查文件。
