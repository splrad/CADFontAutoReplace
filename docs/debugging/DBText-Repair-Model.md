# DBText 封闭式 AI 修复

本文档描述当前 DBText 单行文字修复链路。旧的 JSONL 标签模型、本地合并、本地训练、`AFRDBTEXTLABEL` 人工确认命令和 Debug 批量训练命令已经废弃。

## 当前实现

DBText 修复由封闭式本地 AI 决策链路负责，不依赖在线服务，也不允许普通用户训练或替换模型。文枢不会因为文本“看起来像乱码”、字体缺失或候选转换成功就扫描所有正常文本；必须先命中 native DBCS/code page Hook 强证据，或由已修复强证据种子产生严格等同证据，文枢模型才会介入。

执行入口位于 `ExecutionController.Execute`：

1. 检测并替换文字样式表中的缺失字体。
2. 扫描并处理 MText 内联字体；缺失 TrueType 内联字体可转换为 SHX `\F`，再与 `LdFileHook` 重定向记录交叉生成内联修复记录。
3. 执行 `GlyphCoreTextRepairService.Repair`，扫描非外参块中的 `DBText`。
4. `GlyphCoreTextRepairProblemDetector` 只读取 `NativeDecodeEvidence`；不再根据 `CurrentText` 是否像乱码触发。
5. 未命中 Hook 强证据时静默跳过，不加载模型、不评分、不提示。
6. 命中强证据后生成候选、提取 `dbtext-ai-features-v7` 特征并调用本地 AI 评分器。
7. 同类 DBText 簇共享一次 AI 判断；已修复文本可作为涟漪种子为周边对象提供上下文。

`GlyphCoreNativeDecodeEvidenceStore` 只是内存桥，不会伪造证据；如果当前构建没有 native Hook 生产者注册对象级或簇级证据，DBText 文枢会保持静默。

`dbtext-ai-features-v7` 不再让字体文件名、BigFont 文件名、字体族、typeface 或 `LdFileHook` 字体证据参与模型决策。这些字段会随 AutoCAD 版本、字体映射和用户电脑环境变化；运行时和训练脚本保留对应特征槽位但统一置零，避免同一 DWG 因字体环境差异造成模型分数漂移。

DBText native evidence 链路不再在共享 Hook 中写死 AutoCAD 2025 门槛；各版本由 `AutoCad20XXPlatform` 提供 `NativeDecodeHookProfile`，共享安装器按 profile 安装或 fail closed。当前静态基线完整启用 AutoCAD 2022-2027，2027 的 `readString(wchar**)` 通过 RTTI/vtable 静态确认的 RVA 安装并在运行时校验入口 prefix，AcPal UTF16 probe 通过 2024-2027 的 `acpal#*.dll` 基线启用。AutoCAD 2018-2021 虽可经 RTTI/vtable 定位 `AcDbImpText::dwgInFields`，但缺少完整 readString/code-page resolver 验证，保持 DBText AI native hook fail closed。主要组件是：

- `DwgFilerCodePageScopeHook`：读取 DWG filer 中真实 DBCS code page 作用域。
- `DbTextDwgInFieldsScopeHook`：建立 DBText 反序列化对象级作用域并记录 `AcDbImpText` provenance。
- `DbTextUpstreamDecodeProbeHook` / `TextEditorDbcsDecodeHook`：捕获 DBText 实际 DBCS 解码点、原始 code page、filer code page 和原始字节证据。
- `CodePageFamilyHook`：只记录 code page family mismatch 证据，不写 native context，也不修正文本文字。
- `GlyphCoreNativeDbTextEvidenceProjector`：从托管 `DBText` 找回 native provenance，校验 native/current text 一致后调用 `GlyphCoreNativeDecodeEvidenceStore.RegisterDbTextDecodeEvidence(...)`。

这些 Hook 只生产文枢强信号。最终是否写回仍由候选特征和文枢 AI 决策；`LdFileHook` 只属于字体加载/字体重定向链路，不能作为 DBText AI 启动条件。

## 模型部署

- 官方模型资源名：`AFR.GlyphCore.Model.onnx`。
- 模型清单资源名：`AFR.GlyphCore.ModelManifest.json`。
- 不再发布精确修复表；DBText 修复必须由本地 AI 模型独立评分决策。
- 模型清单和 ONNX metadata 应包含文枢身份信息：`aiDisplayName=文枢`、`aiInternalName=GlyphCore`，以及作者 `splrad 秋夕寻星`。
- Release 构建通过私有 MSBuild 属性注入模型，不从仓库读取训练数据。
- 没有嵌入模型、清单缺失或特征 schema 不匹配时，仅在命中 Hook 强证据后提示模型不可用；无强证据时保持静默。

示例：

```powershell
./tools/Publish-ReleaseAssets.ps1 `
  -GlyphCoreModelPath C:\PrivateAFR\Models\AFR.GlyphCore.Model.onnx `
  -GlyphCoreModelManifestPath C:\PrivateAFR\Models\AFR.GlyphCore.ModelManifest.json `
  -GlyphCoreRuntimeDirectory C:\PrivateAFR\OnnxRuntime\win-x64
```

生产 ONNX 模型、训练数据和用户 DWG 属于开发者私有资产，不提交 GitHub；训练脚本和工作台源码位于 `AFR.GlyphCore/tools`，可以作为工具链代码进入仓库。

## 自动修复规则

`GlyphCoreTextRepairCandidateGenerator` 生成确定性候选：

- 当前文本自身，用作 no-op 候选。
- Big5 / GBK / UTF-8 方向的可逆 carrier 转换候选。
- 如果 Hook 证据表明“源 code page family 被某个目标 family 错解码”，对应的反向候选会优先排序。
- 仅保留非空、去重后的候选。

`GlyphCoreTextRepairProblemDetector` 是文枢介入门控：

- 强信号来自 native Hook 数据流中可证明 `DBCS/code page family mismatch` 的对象级或簇级证据。
- `LdFileHook` 字体重定向记录只作为辅助字体上下文，不作为乱码强信号。
- 控制字符、替代字符、扩展拉丁 mojibake 外观、候选中文比例提升等文本外观特征不再触发 AI。
- 从强证据修复种子产生的 `ripple` / `document-family` 等同证据可以触发文枢，但仍必须继承 `NativeDecodeFamilyMismatch`；不能从字体 Hook、训练集命中、文本外观或手写字符串规则产生。
- 未命中门控时不加载 ONNX 模型、不做 AI 评分、不向命令行输出 DBText 提示。

`GlyphCoreTextRepairAdvisor` 只负责编排：

1. 提取候选特征。
2. 调用嵌入 ONNX 评分器。
3. 把候选分数交给 `GlyphCoreTextRepairDecisionEngine`。

自动写回规则：

- Hook native 解码强证据已命中；
- AI 模型可用；
- AI 选择的最佳候选不是当前文本；
- 候选不包含私用区、注音/假名、未分配字符、替代字符或非工程符号；
- 对象不来自外参或依赖块。

Decision Engine 的默认路径仍以文枢模型在 no-op 与修复候选之间的选择为准。若 native evidence 明确、候选方向与 code page family mismatch 一致、候选可逆且文本安全，运行时允许保守接受低置信度、小 margin 或 `current-noop` 微弱领先场景，避免 CAD 版本差异造成已确认乱码漏修。

命令行提示规则：

- 无 Hook 强证据：不提示。
- 有强证据但模型不可用：`[AFR 文枢] 当前 文枢 决策模型不可用；未执行 DBText 自动修复。`
- 有强证据但 AI 选择不写回：`[AFR 文枢] 检测到 DBText native 解码强信号；文枢 AI 选择不写回。`
- 写回成功：`[AFR 文枢] 检测到 DBText native 解码强信号；文枢 已完成 AI 决策并成功修复 N 项。`

## 用户反馈

用户端不提供 DBText 标注、反馈包导出、训练、导入样本或模型替换功能。

如果用户发现 DBText 乱码未修复或误修，处理方式是直接联系开发者，并发送：

- 问题 DWG；
- 必要截图；
- 现象描述；
- AutoCAD 版本和 AFR 版本。

开发者在私有环境中提取样本、人工审核、训练 LightGBM / XGBoost / FastTree 模型，并随新版 DLL 发布。

## 数据与标注边界

导出命令、网页工作台、训练集和模拟报告必须以 CAD 当前实际文本为准。`displayText` 只是展示当前文本，不再把 `井` / `#` 当成显示别名互相归一化。

典型例子：

- 如果本图纸中 `FL-井1` 是正确文本，人工标注应保留 `FL-井1`。
- 如果另一张图纸中同位置语义应为 `FL-#1`，人工标注可以标为 `FL-#1`。

不能在运行时、训练脚本或工作台中写死 `井 -> #` 或 `# -> 井` 限制规则；这会把某一张图纸的局部语义错误扩散到其它图纸。正确做法是保留真实文本、坐标、图层、块、样式、编码路径和候选证据，由文枢模型结合上下文学习最终 keep/repair 决策。

## 边界

- 不要恢复 `data/DbTextRepairModel.jsonl` 作为运行时输入。
- 不要恢复 `AFRDBTEXTLABEL`、本地训练、JSONL 合并或用户端模型替换入口。
- 不要把 DBText 编码修复做成全局字符串替换。
- 不要把读取出来的 DBText 文本是否像乱码作为强信号；强信号只能来自 native Hook 证据流或由已修复强证据种子产生的涟漪证据。
- 不要把 `LdFileHook` 字体重定向记录当成 DBText 错解码证明。
- 不要在导出、工作台、训练或运行时增加 `井/#` 这类硬编码显示别名或禁修规则。
- 字形缺笔、SHX 字体形状问题仍应走字体/样式诊断，不应强行写回 DBText 文本。

## 提交前检查

- Release DLL 不包含 `DbTextRepairModel.jsonl`。
- Release DLL 不包含 `AFRDBTEXTLABEL`、`AFRDBTEXTBATCHTRAIN`、`AFRGLYPHCOREEXPORT` 或 `AFRGLYPHCOREEXPORTSELECT`。
- 无官方 ONNX 模型时 DBText 修复只在 Hook 强证据命中后提示模型不可用。
- 无 Hook 强证据图纸不应加载文枢模型，也不应输出 DBText 文枢提示。
- 训练集和盲测集必须包含正常简体中文、英文、数字、符号、混合文本和真实错解码文本。
- `dbtext-ai-features-v7` schema、ONNX manifest、导出包 schema 和训练脚本必须保持一致。
- `AFRLOG` 仍只展示字体替换结果，不作为 DBText AI 纠错入口。
