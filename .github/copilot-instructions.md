# CADFontAutoReplace 仓库记忆

本文档是本仓库的长期协作记忆。它应跟随代码进入 GitHub，用于约束后续 AI/Copilot/Codex 修改方向。若代码实现变化，优先更新本文档和相关公开文档，再删除或更正过期调查文档。

## 当前项目事实

- 项目名：`CADFontAutoReplace`，简称 AFR。
- 主用途：为 AutoCAD 图纸自动处理缺失字体、MText 内联字体引用，以及由文枢本地 AI 驱动的 DBText 单行文字乱码保守修复。
- 支持 AutoCAD 版本：2018 到 2027。
- 主要分发方式：`AFR.Deployer` 部署工具一键安装/卸载。
- 次要分发方式：单 DLL `NETLOAD`，用于维护、测试和受限环境。
- 统一版本来源：根目录 `Version.props`。
- 公开说明入口：`README.md`、`docs/developer-guide-beginner.md`、`docs/developer-guide-advanced.md`。
- 文枢 DBText 开发入口：`AFR.GlyphCore/tools/README.md`、`AFR.GlyphCore/tools/Start-GlyphCoreWorkbench.ps1`、`AFR.GlyphCore/tools/Invoke-GlyphCoreTraining.ps1`。

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
- 文枢 DBText 的核心候选、特征、评分和决策逻辑属于 `AFR.Core/GlyphCore/TextRepair`；AutoCAD 对象遍历、上下文快照、写回和命令行反馈属于 `AFR.AutoCAD/Services/GlyphCore/TextRepair`。

## 当前命令

Release 命令：

- `AFR`：字体配置。
- `AFRLOG`：字体替换日志与手动样式调整界面。
- `AFRUNLOAD`：隐藏维护入口，不通过 `CommandMethod` 注册；只在完整输入时由 `UnknownCommand` 路由触发。

Debug 命令：

- `AFRVIEW`：查看 MText / MLeader 格式与样式诊断。
- `AFRINSERT`：插入测试 MText。
- `AFRDUMPPROFILE`、`AFRSHOWAWSPATH`、`AFRGENPROBESCRIPTS`、`AFRDUMPDIALOGAPI`：调试/反射辅助命令。
- `AFRGLYPHCOREEXPORT`：Debug-only，全图导出文枢 DBText AI 分析数据包。
- `AFRGLYPHCOREEXPORTSELECT`：Debug-only，使用 CAD 原生选择集选择 DBText，确认后导出文枢 DBText AI 分析数据包；取消、空选择或非 DBText 不导出。

新增命令时：

- 先在 `src/AFR.Core/Constants/CommandNames.cs` 登记。
- 常规命令需要在版本壳 `PluginEntry.cs` 注册 `CommandClass`。
- Debug-only 命令必须在命令文件和注册处同时用 `#if DEBUG` 控制。
- `AFRUNLOAD` 这类隐藏入口不要注册进 CAD 命令补全体系。
- 不要恢复 `AFRDBTEXTLABEL`、`AFRDBTEXTBATCHTRAIN`、`AFRDBTEXTMODEL`、`AFRINSPECTTEXT`、DBText 用户端样本导入或用户端模型替换命令；当前训练/复核入口在本地文枢工具链，不是 Release CAD 命令。

## 字体替换链路

主链路在 `ExecutionController.Execute`：

1. 使用 `FontDetector` 检测样式表缺失字体。
2. 使用 `FontReplacer` 替换 SHX 主字体、SHX 大字体与 TrueType。
3. 执行文枢 DBText 本地 AI 修复。
4. 扫描 MText 内联字体。
5. 用 `LdFileHook` 重定向记录与 MText 正向扫描结果交叉生成修复记录。
6. 输出摘要并刷新显示。

注意事项：

- `AFRLOG` 不是通用 DiagnosticLogger 查看器；它主要展示字体表替换和内联字体替换的 UI。
- TrueType 样式表替换必须继续使用 TrueType，避免污染 AutoCAD 字体缓存。
- MText 内联缺失 TrueType 可转换为 SHX `\F` 格式，让后续渲染走 SHX/Hook 路径。
- SHX 字形缺笔、字体形状不正确，与 DBText 编码修复是两类问题，不要混在同一修复策略里。

## 文枢 DBText 本地 AI 修复

当前代码不再使用旧的原生 code page Hook 探针作为公开 DBText 修复路径。

当前实现：

- 自动修复入口：`src/AutoCAD/AFR.AutoCAD/Services/GlyphCore/TextRepair/GlyphCoreTextRepairService.cs`。
- AutoCAD 上下文快照：`GlyphCoreTextRepairEntitySnapshotBuilder`、`GlyphCoreDrawingIdentity`。
- AutoCAD 侧 AI 适配：`src/AutoCAD/AFR.AutoCAD/Services/GlyphCore/TextRepair/GlyphCoreTextRepairAdvisor.cs`。
- 疑似异常门控：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairProblemDetector.cs`。
- 候选生成：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairCandidateGenerator.cs`。
- 特征提取：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairFeatureExtractor.cs`，schema 为 `dbtext-ai-features-v1`。
- 本地评分：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairEmbeddedOnnxScorer.cs`，只从 DLL 嵌入资源加载 ONNX、模型清单和 ONNX Runtime 资源。
- 精确修复表：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairExactRepairLookup.cs`，资源名为 `AFR.GlyphCore.ExactRepairs.json`，用于训练集中完全匹配的保守修复。
- 自动决策：`GlyphCoreTextRepairDecisionEngine`，阈值来自 `GlyphCoreTextRepairConstants.MinimumConfidence = 0.92` 与 `MinimumScoreMargin = 0.18`。
- 模型接口与数据结构：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairModels.cs`。

自动写回规则：

- 未检测到疑似 DBText 异常时保持静默，不加载文枢模型、不评分、不提示。
- 检测到疑似异常后，候选来自原文、Big5/GBK/UTF-8 可逆转换、安全回退、已知乱码模式和保守候选。
- 文枢优先使用精确修复表；没有精确匹配时才使用嵌入 ONNX 模型评分。
- 无模型、模型不匹配、低置信度、分差过小、AI 选择原文、候选冲突、不可逆转换、控制字符、异常 Unicode、Xref 或依赖块、高风险文本时，一律跳过写回。
- 写回只修改通过 `ShouldRepair` 的 `DBText.TextString`；所有跳过、阻断和修复结果写入 DiagnosticLogger 与命令行摘要。
- 保持“宁可不修，也不能误修”的保守原则；不要添加强制修复所有文本的逻辑。

## 文枢 DBText 数据与训练工具

开发者训练链路在 `AFR.GlyphCore/tools`，不是普通用户功能。

当前布局：

```text
AFR.GlyphCore/tools/
  Start-GlyphCoreWorkbench.ps1
  Invoke-GlyphCoreTraining.ps1
  workbench/server.py
  workbench/frontend/
  training/*.py
  afr_glyphcore/
  schemas/

AFR.GlyphCore/datasets/
  ExtractedCandidates/    # local-only
  ReviewedLabels/         # local-only
  TrainingSets/           # local-only
  Reports/                # local-only

AFR.GlyphCore/models/
  AFR.GlyphCore.Model.onnx             # local-only
  AFR.GlyphCore.ModelManifest.json     # local-only
  AFR.GlyphCore.ExactRepairs.json      # local-only
  AFR.GlyphCore.Model.txt              # local-only
  *_validation_report.json            # local-only
```

数据规则：

- `AFR.GlyphCore/tools` 下的工具、schema、Python 代码、React/Vite 前端和测试可以进入 Git。
- `AFR.GlyphCore/datasets`、`AFR.GlyphCore/models`、`AFR.GlyphCore/raw-dwg`、用户 DWG/DXF、训练产物和模型产物必须保持本地私有，不上传 GitHub。
- 训练数据、Reviewed JSONL、TrainingSets、Reports、ONNX 模型和精确修复表只在本地开发者环境流转。
- 如果训练资产曾经被加入索引，优先使用 `git rm --cached` 类方式移出索引，不删除本地文件。
- `.github/copilot-instructions.md` 是真正的仓库记忆文件，不应加入 `.gitignore`。

导出规则：

- `AFRGLYPHCOREEXPORT` 保留为全图批量导出命令。
- `AFRGLYPHCOREEXPORTSELECT` 保留为无 UI 的手动多选导出命令，使用 CAD 原生选择和确认，不弹 WPF 窗口。
- 两个命令共用 `GlyphCoreDatasetExporter.ExportCore(...)`，输出 `manifest.json`、`candidate_groups.jsonl`、`preview.json` 和 `audit.tsv`。
- `export_package_v1.schema.json` 的 `commandName` 必须允许 `AFRGLYPHCOREEXPORT` 与 `AFRGLYPHCOREEXPORTSELECT`。
- 数据集默认根目录可由 `AFR_GLYPHCORE_DATASET_ROOT` 覆盖，否则走仓库本地数据目录。

工作台规则：

- `AFR.GlyphCore/tools/Start-GlyphCoreWorkbench.ps1` 是本地浏览器工作台入口。
- `workbench/server.py` 是 Python 本地 API 与静态文件服务；`workbench/frontend` 是 React/Vite 前端，改前端后需要 `npm run build` 生成 `dist`。
- 标注工作流以人工表格复核为主，只有 `未审核` / `已审核` 两类状态；已审核行允许再次编辑覆盖。
- 10k+ 重复文本默认按文本簇处理，簇键由 current text、推荐 candidate text、candidate source、recommended action 组成；layer、style、font、block、xref、risk 只是上下文摘要或风险提示，不应作为主要拆分键。
- 审核一个簇后，仍要展开写入一条 reviewed JSONL 记录到每个 DBText 实体，保持 feature 生成和训练兼容。
- 传播审计字段如 `propagationClusterId`、`propagationSignature`、`clusterRiskSummary`、`clusterContextSummary`、`propagationScope`、`propagationRule` 只是训练审计元数据，不写回 DWG。
- 普通/安全文本也需要作为 keep 样本进入训练，但必须通过人类确认或表格批量确认，不应自动覆盖审核判断。
- Feature 只能从 reviewed JSONL / training dataset 生成，未审核 candidate 不应直接进入训练。
- 删除 training dataset 记录后，应能重新回流到待复核队列，并同步更新 features。

训练规则：

- `Invoke-GlyphCoreTraining.ps1` 是命令行训练入口。
- `training/build_features.py` 从 reviewed labels 生成 `dbtext-ai-features-v1` CSV。
- `training/train_lightgbm.py` 训练当前模型，输出 ONNX、模型清单、精确修复表和验证报告。
- `workbench/test_review_clusters.py` 覆盖簇传播、已审核覆盖、training dataset 提升/删除/回流等核心行为。
- 训练流程可以为效率做批量处理，但必须保留可审计的 reviewed JSONL、audit TSV 和训练摘要。

模型嵌入规则：

- `src/AutoCAD/Directory.Build.targets` 会通过 `GlyphCoreModelPath`、`GlyphCoreModelManifestPath`、`GlyphCoreExactRepairsPath`、`GlyphCoreRuntimeDirectory` 注入私有模型资源。
- 如果仓库本地存在 `AFR.GlyphCore/models` 下的模型文件，构建也可自动发现并嵌入。
- GitHub Release 工作流和公开仓库不得包含真实训练数据、用户 DWG、ReviewedLabels、TrainingSets、Reports 或生产模型文件。
- 普通用户不能训练、导入样本、替换模型、修改参数、人工标注或上传反馈包。

## 已删除或废弃的旧 DBText 调查内容

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
- `DbTextRepairModel.jsonl` 作为 Release 用户端可变学习库
- CAD 内置的 DBText 用户端标注、批量训练、模型查看、样本导入或模型替换命令

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
- DBText AI 模型只从开发者私有路径注入，公开仓库和发布资产脚本不得包含训练数据或可替换模型入口。

## 文档维护规则

- README 面向使用者，默认推荐部署器安装；单 DLL/NETLOAD 只作为补充路径。
- 开发者指南说明构建、调试、命令注册、发布资产和文枢 DBText 封闭式 AI 模型维护。
- 文枢开发工具文档以 `AFR.GlyphCore/tools/README.md` 为准；若 `docs/debugging/DBText-Repair-Model.md` 或开发者指南仍描述旧 JSONL 用户端训练/旧 CAD 标注命令，应按当前 `GlyphCoreTextRepair*` 代码刷新。
- Debug 调查文档必须只保留当前代码仍存在的命令、类和流程。
- 本地临时日志、构建产物、浏览器 profile、截图、反汇编结果不要进入 GitHub。
- `.github/copilot-instructions.md` 是真正的仓库记忆文件，不要加入 `.gitignore`。

## 提交前检查

- `dotnet build CADFontAutoReplace.slnx` 能通过，或明确说明本机缺失 SDK/AutoCAD 依赖。
- 发布相关变更应验证 `tools/Publish-ReleaseAssets.ps1`。
- DBText AI 变更应验证候选生成、特征稳定性、安全阻断、模型缺失跳过、schema 不匹配跳过、Release DLL 无旧用户端训练/标注入口。
- GlyphCore 导出命令变更应验证 Debug 构建包含 `AFRGLYPHCOREEXPORT` / `AFRGLYPHCOREEXPORTSELECT`，Release 构建不包含这两个命令。
- Workbench 变更应运行 `AFR.GlyphCore/tools/workbench/test_review_clusters.py`，必要时通过真实浏览器验证 `Start-GlyphCoreWorkbench.ps1` 的可见交互路径。
- 前端变更应在 `AFR.GlyphCore/tools/workbench/frontend` 运行 `npm run build` 并确认 `dist` 与 `server.py` 静态服务路径一致。
- 新增文档必须能从 README、开发者指南或 `AFR.GlyphCore/tools/README.md` 找到入口，除非它明确是本地临时调查文件。
