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

1. 使用 `FontDetector` 检测样式表普通缺失字体，并由 `FontReplacer.ReplaceMissingFonts()` 执行永久替换。
2. 替换后重新检测并存储仍缺失结果，供 `AFRLOG` 标记当前状态。
3. 使用 `StyleTextStyleHook` 只对样式表带 `@` 的缺失字体执行临时运行时映射。
4. 扫描 MText 内联字体；`MTextInlineFontHook` 只在 MText 作用域内处理全部内联缺失字体，并直接记录实际映射结果。
5. 执行文枢 DBText 本地 AI 修复，最后输出摘要并刷新显示。

当前 Hook 职责边界：

- `StyleTextStyleHook` 只负责样式表带 `@` 的缺失字体运行时映射。它 Hook `AcGiTextStyle::loadStyleRec`，并解析/调用 `styleName`、`fileName`、`bigFontFileName`、`isVertical`、`setFont`、`setFileName`、`setBigFontFileName`、`setVertical`。
- `MTextInlineFontHook` 只负责 MText 内联缺失字体运行时映射。它 Hook `AcDbMText::explodeFragments` 建立 MText 作用域，在该作用域内处理 `AcGiTextStyle::setFont`、`AcGiTextStyle(font,bigFont)`、`setFileName`、`setBigFontFileName`。
- `LdFileHook` 已从 Release 主链路删除；字体存在性与 SHX 类型判断统一由 `FontAvailabilityIndex` 提供，运行时命中记录统一由 `FontRuntimeMappingStore` 提供。
- 样式表运行时操作必须进入 `StyleTextStyleHook.EnterStyleRuntimeOperation()` 作用域；`MTextInlineFontHook` 在 `StyleTextStyleHook.IsInsideStyleRuntimeOperation` 为 true 时必须旁路，避免样式表主动加载触发 MText Hook 日志或重定向。
- MText Hook 不能处理样式表字体；样式表 Hook 不能处理 MText 内联字体。两者都可以使用同一套字体可用性、`@` 前缀剥离和配置兜底规则，但不能混用触发来源。

样式表运行时映射规则：

- 样式表非 `@` 缺失字体必须走 `FontReplacer.ReplaceMissingFonts()` 永久替换；只有带 `@` 的样式表缺失字体由 `StyleTextStyleHook` 做运行时映射，样式表原始 `FileName`、`BigFontFileName`、`Font.TypeFace` 保持不变。
- 带一个或多个 `@` 前缀的样式表字体必须一次性去除所有连续前缀后优先检查本机真实字体；本机存在时映射到去 `@` 后字体，本机不存在时映射到配置替换字体。
- 样式表 `@TrueType` 去 `@` 后本机字体存在时跳过映射，交给 CAD 原生显示；样式表 `@SHX` 去 `@` 后本机字体存在且主/大字体类型匹配时临时映射到真实 SHX。
- 带 `@` 前缀的样式表字体只能运行时临时映射，不允许永久替换。
- `StyleTextStyleHook` 必须在原始 `loadStyleRec` 执行前应用登记映射；登记后主动对相关样式执行 managed `Autodesk.AutoCAD.GraphicsInterface.TextStyle.FromTextStyleTableRecord(id)` 与 `LoadStyleRec`，确保未自然触发显示加载的 SHX 样式也能命中 Hook。
- 已验证日志证据：`AFR_Diag_20260520_020305.log` 中 `10_TrueType竖写字体格式.dwg` 的对账结果为 `主动加载样式完成: attempted=5, loaded=5`，`样式表Hook对账: 替换逻辑缺失槽位=5, 运行时登记=6, Hook总命中=6, Hook命中缺失槽位=5, 字体名一致=True, 未命中=0, 额外命中=1`。额外 1 项是 `黑体竖写:@黑体`，属于预期 @TrueType 运行时映射。

MText 内联运行时映射规则：

- MText 内联缺失字体不再通过 `MTextInlineFontReplacer` 改写 `MText.Contents`，不要恢复缺失 TrueType 内联字体 `TrueType -> SHX \F` 的内容转换器。
- MText 内联 TrueType 缺失必须保持 TrueType -> TrueType 运行时映射；MText 内联 SHX 主字体和 SHX 大字体按 SHX 类型映射。
- MText 内联字体名带一个或多个 `@` 前缀时，先一次性去除所有连续 `@` 得到真实字体名；本机存在真实字体时优先映射到真实字体，本机不存在时映射到配置替换字体。
- MText 内联扫描结果用于触发显示刷新和辅助展示；AFRLOG 展示记录必须来自 `MTextInlineFontHook` 实际命中的业务映射结果，不能靠盲猜或直接改写内容制造记录。

注意事项：

- `AFRLOG` 不是通用 DiagnosticLogger 查看器；它主要展示样式表字体运行时映射、保留的手动样式调整入口，以及 MText 内联字体映射记录。
- TrueType 样式表替换必须继续使用 TrueType，避免污染 AutoCAD 字体缓存。
- 样式表缺失字体运行时映射后，样式表仍保留原值；AFRLOG 应显示为“运行时已映射 / 样式表保持原值”，不能继续按普通“未替换”理解。
- MText 内联缺失 TrueType 不允许再转换为 SHX `\F` 格式。
- 文枢 DBText 修复必须位于样式表替换和 MText 内联处理之后，让 DBText 决策运行在更稳定的字体上下文中。
- SHX 字形缺笔、字体形状不正确，与 DBText 编码修复是两类问题，不要混在同一修复策略里。

## 文枢 DBText 本地 AI 修复

当前代码不再使用读取到的文字外观作为 DBText 乱码强信号；文枢介入必须来自 native DBCS/code page Hook evidence，或由已修复强证据种子产生的涟漪/同文档等同强信号。DBText native Hook 与字体运行时映射是两条独立链路；字体缺失或字体映射记录不得作为 DBText AI 启动条件。

当前实现：

- 自动修复入口：`src/AutoCAD/AFR.AutoCAD/Services/GlyphCore/TextRepair/GlyphCoreTextRepairService.cs`。
- AutoCAD 上下文快照：`GlyphCoreTextRepairEntitySnapshotBuilder`、`GlyphCoreDrawingIdentity`。
- AutoCAD 侧 AI 适配：`src/AutoCAD/AFR.AutoCAD/Services/GlyphCore/TextRepair/GlyphCoreTextRepairAdvisor.cs`。
- native 解码 evidence 内存桥：`GlyphCoreNativeDecodeEvidenceStore`。
- native evidence 投影：`GlyphCoreNativeDbTextEvidenceProjector`，从托管 `DBText` 找回 `AcDbImpText` provenance，校验 native/current text 一致后注册证据。
- DBText native Hook 生产者：`DwgFilerCodePageScopeHook`、`DbTextDwgInFieldsScopeHook`、`DbTextUpstreamDecodeProbeHook`、`TextEditorDbcsDecodeHook`、`CodePageFamilyHook`。这些 Hook 只生产文枢强信号证据，不直接修改文字、不直接改 native code page。
- Hook 强信号门控：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairProblemDetector.cs`。
- 候选生成：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairCandidateGenerator.cs`。
- 特征提取：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairFeatureExtractor.cs`，schema 为 `dbtext-ai-features-v7`。
- 本地评分：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairEmbeddedOnnxScorer.cs`，只从 DLL 嵌入资源加载 ONNX、模型清单和 ONNX Runtime 资源。
- 自动决策：`GlyphCoreTextRepairDecisionEngine`，不再使用固定低置信度或分差门槛替 AI 做最终判断。
- 模型接口与数据结构：`src/AFR.Core/GlyphCore/TextRepair/GlyphCoreTextRepairModels.cs`。

自动写回规则：

- 未检测到 native DBCS/code page Hook 强证据时保持静默，不加载文枢模型、不评分、不提示。
- `GlyphCoreNativeDecodeEvidenceStore` 只消费内存证据，不伪造证据；没有 native Hook 生产者或等同强信号种子时 DBText 文枢不会触发。
- 字体运行时映射记录只能作为辅助字体上下文，不得作为 DBText 错解码强信号。
- 检测到强证据后，候选来自原文、Big5/GBK/UTF-8 carrier 转换，并按 Hook 证据方向优先排序。
- 同类文本簇共享一次 AI 判断；涟漪和同文档 family 扩散只能从已修复且有 native family mismatch 强证据的 DBText 种子产生，不能从文本外观、字体缺失、训练集命中或候选转换成功产生。
- 文枢只使用嵌入 ONNX 模型评分，不再使用精确修复表或训练集查表短路。
- 无模型、模型不匹配、AI 选择原文、Xref 或依赖块、写回失败时跳过写回。
- 写回只修改通过 `ShouldRepair` 的 `DBText.TextString`；所有跳过、阻断和修复结果写入 DiagnosticLogger 与命令行摘要。
- 普通用户端不落盘候选、分数、证据或审计记录；Debug 导出命令可导出 evidence 字段用于离线训练。

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
  AFR.GlyphCore.Model.onnx             # tracked build input
  AFR.GlyphCore.ModelManifest.json     # tracked build input
  AFR.GlyphCore.Model.txt              # local-only
  AFR.GlyphCore.TrainingState.json     # local-only
  candidates/                          # local-only
  .trash/                              # local-only
  *_validation_report.json            # local-only
```

数据规则：

- `AFR.GlyphCore/tools` 下的工具、schema、Python 代码、React/Vite 前端和测试可以进入 Git。
- `AFR.GlyphCore/models/AFR.GlyphCore.Model.onnx` 和 `AFR.GlyphCore/models/AFR.GlyphCore.ModelManifest.json` 是最终构建输入，必须允许进入 GitHub。
- `AFR.GlyphCore/datasets`、`AFR.GlyphCore/raw-dwg`、用户 DWG/DXF、ReviewedLabels、TrainingSets、Reports、候选模型、训练状态、验证报告和 `.trash` 归档必须保持本地私有，不上传 GitHub。
- 如果训练资产曾经被加入索引，优先使用 `git rm --cached` 类方式移出索引，不删除本地文件；不要把当前发布模型文件移出索引。
- `.github/copilot-instructions.md` 是真正的仓库记忆文件，不应加入 `.gitignore`。
- 导出、工作台、训练和报告必须保留 CAD 当前实际文本；`displayText` 只能展示当前文本，不再做 `井` / `#` 等显示别名归一化。`FL-井1` 与 `FL-#1` 是两条不同语义标签，不能在运行时或训练层写死互相替换限制，应由人工标注数据和文枢模型结合上下文决策。

导出规则：

- `AFRGLYPHCOREEXPORT` 保留为全图批量导出命令。
- `AFRGLYPHCOREEXPORTSELECT` 保留为无 UI 的手动多选导出命令，使用 CAD 原生选择和确认，不弹 WPF 窗口。
- 两个命令共用 `GlyphCoreDatasetExporter.ExportCore(...)`，输出 `manifest.json`、`candidate_groups.jsonl`、`preview.json` 和 `audit.tsv`。
- `export_package_v1.schema.json` 的 `commandName` 必须允许 `AFRGLYPHCOREEXPORT` 与 `AFRGLYPHCOREEXPORTSELECT`。
- 数据集默认根目录可由 `AFR_GLYPHCORE_DATASET_ROOT` 覆盖，否则走仓库本地数据目录。

工作台规则：

- `AFR.GlyphCore/tools/Start-GlyphCoreWorkbench.ps1` 是本地浏览器工作台入口。
- `workbench/server.py` 是 Python 本地 API 与静态文件服务；`workbench/frontend` 是 React/Vite 前端，改前端后需要 `npm run build` 生成 `dist`。
- 当前生产工作台是四页签：`数据标注`、`训练数据集`、`模型训练`、`模型报告`。不要再恢复旧的 `数据包` / `人工复核` / `特征生成` 拆分导航。
- 标注工作流以人工表格复核为主，只有 `未审核` / `已审核` 两类状态；已审核行允许再次编辑覆盖。
- 10k+ 重复文本默认按文本簇处理，簇键由 current text、推荐 candidate text、candidate source、recommended action 组成；layer、style、font、block、xref、risk 只是上下文摘要或风险提示，不应作为主要拆分键。
- 审核一个簇后，仍要展开写入一条 reviewed JSONL 记录到每个 DBText 实体，保持 feature 生成和训练兼容。
- 传播审计字段如 `propagationClusterId`、`propagationSignature`、`clusterRiskSummary`、`clusterContextSummary`、`propagationScope`、`propagationRule` 只是训练审计元数据，不写回 DWG。
- 普通/安全文本也需要作为 keep 样本进入训练，但必须通过人类确认或表格批量确认，不应自动覆盖审核判断。
- Feature 只能从 reviewed JSONL / training dataset 生成，未审核 candidate 不应直接进入训练。
- 删除 training dataset 记录后，应能重新回流到待复核队列，并同步更新 features。

训练规则：

- `Invoke-GlyphCoreTraining.ps1` 是命令行训练入口。
- `training/build_features.py` 从 reviewed labels / training dataset 生成 `dbtext-ai-features-v7` CSV。
- `training/train_lightgbm.py` 训练当前模型，输出 ONNX、模型清单和验证报告；不会生成 `AFR.GlyphCore.ExactRepairs.json`。
- `workbench/test_review_clusters.py` 覆盖簇传播、已审核覆盖、training dataset 提升/删除/回流等核心行为。
- 训练流程可以为效率做批量处理，但必须保留可审计的 reviewed JSONL、audit TSV 和训练摘要。

模型嵌入规则：

- `src/AutoCAD/Directory.Build.targets` 默认从仓库中的 `AFR.GlyphCore/models/AFR.GlyphCore.Model.onnx` 和 `AFR.GlyphCore.ModelManifest.json` 嵌入当前发布模型。
- `GlyphCoreModelPath`、`GlyphCoreModelManifestPath`、`GlyphCoreRuntimeDirectory` 只用于需要覆盖默认模型或运行时目录的私有构建。
- ONNX Runtime 原生依赖按 ABI 共享到插件 DLL 同级的 `OnnxRuntime/<abiKey>/`，由插件启动和部署器安装流程共同检查/补齐，不按 AFR 插件版本号隔离。
- GitHub Release 工作流和公开仓库不得包含真实训练数据、用户 DWG、ReviewedLabels、TrainingSets、Reports、候选模型、训练状态或验证报告。
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
- 旧 native Hook 调查结论中“Hook 直接修文本/直接改 code page”的做法
- `DbTextRepairModel.jsonl` 作为 Release 用户端可变学习库
- CAD 内置的 DBText 用户端标注、批量训练、模型查看、样本导入或模型替换命令

当前 native code page 链路已经恢复为证据生产链路；后续只能扩展证据覆盖面，不能恢复旧的直接写回、直接改 native code page 或用户端探针流程。

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
- DBText AI 默认从仓库跟踪的发布模型注入；公开仓库和发布资产脚本不得包含训练数据、候选模型或可替换模型入口。

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
- DBText AI 变更应验证 native Hook evidence 门控、无强证据静默、候选生成、`dbtext-ai-features-v7` 特征稳定性、模型缺失跳过、schema 不匹配跳过、Release DLL 无旧用户端训练/标注入口。
- GlyphCore 导出命令变更应验证 Debug 构建包含 `AFRGLYPHCOREEXPORT` / `AFRGLYPHCOREEXPORTSELECT`，Release 构建不包含这两个命令。
- Workbench 变更应运行 `AFR.GlyphCore/tools/workbench/test_review_clusters.py`，必要时通过真实浏览器验证 `Start-GlyphCoreWorkbench.ps1` 的可见交互路径。
- 前端变更应在 `AFR.GlyphCore/tools/workbench/frontend` 运行 `npm run build` 并确认 `dist` 与 `server.py` 静态服务路径一致。
- 新增文档必须能从 README、开发者指南或 `AFR.GlyphCore/tools/README.md` 找到入口，除非它明确是本地临时调查文件。
