# DBText 封闭式 AI 修复

本文档描述当前 DBText 单行文字修复链路。旧的 JSONL 标签模型、本地合并、本地训练、`AFRDBTEXTLABEL` 人工确认命令和 Debug 批量训练命令已经废弃。

## 当前实现

DBText 修复由封闭式本地 AI 决策链路负责，不依赖在线服务，也不允许普通用户训练或替换模型。AI 不扫描所有正常文本；必须先由疑似异常门控命中，文枢模型才会介入。

执行入口位于 `ExecutionController.Execute`：

1. 检测并替换文字样式表中的缺失字体。
2. 执行 `GlyphCoreTextRepairService.Repair`，扫描非外参块中的 `DBText`。
3. `GlyphCoreTextRepairProblemDetector` 判断当前 DBText 是否存在疑似异常。
4. 未命中门控时静默跳过，不加载模型、不评分、不提示。
5. 命中门控后才生成候选、提取 `dbtext-ai-features-v1` 特征并调用本地 AI 评分器。
6. 由 Decision Engine 按保守策略决定写回或跳过。
7. 扫描并处理 MText 内联字体。

## 模型部署

- 官方模型资源名：`AFR.GlyphCore.Model.onnx`。
- 模型清单资源名：`AFR.GlyphCore.ModelManifest.json`。
- 不再发布精确修复表；DBText 修复必须由本地 AI 模型独立评分决策。
- 模型清单和 ONNX metadata 应包含文枢身份信息：`aiDisplayName=文枢`、`aiInternalName=GlyphCore`，以及作者 `splrad 秋夕寻星`。
- Release 构建通过私有 MSBuild 属性注入模型，不从仓库读取训练数据。
- 没有嵌入模型、清单缺失或特征 schema 不匹配时，仅在检测到疑似 DBText 异常后提示模型不可用；无疑似异常时保持静默。

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
- 仅保留非空、去重后的候选。

`GlyphCoreTextRepairProblemDetector` 是文枢介入门控：

- 控制字符、替代字符或异常 Unicode 直接视为疑似异常。
- 典型 mojibake 扩展拉丁字符模式视为疑似异常。
- 可逆编码候选显著提升中文比例或 CAD 词汇比例时视为疑似异常。
- 未命中门控时不加载 ONNX 模型、不做 AI 评分、不向命令行输出 DBText 提示。

`GlyphCoreTextRepairAdvisor` 只负责编排：

1. 提取候选特征。
2. 调用嵌入 ONNX 评分器。
3. 把候选分数交给 `GlyphCoreTextRepairDecisionEngine`。

自动写回必须同时满足：

- 疑似异常门控已命中；
- AI 模型可用；
- 最佳候选不是当前文本；
- 最佳候选分数不低于默认阈值；
- 最佳候选与第二名分差足够大；
- 候选转换可逆；
- 当前文本和候选文本都没有控制字符、替代字符或异常 Unicode；
- 对象不来自外参或依赖块。

命令行提示规则：

- 无疑似异常：不提示。
- 有疑似异常但模型不可用：`[AFR 文枢] 当前 文枢 决策模型不可用；未执行 DBText 自动修复。`
- 有疑似异常但未写回：`[AFR 文枢] 检测到疑似 DBText 异常；当前候选结果未通过 文枢 安全校验，未执行写回。`
- 写回成功：`[AFR 文枢] 检测到疑似 DBText 乱码；文枢 已通过安全校验并成功修复 N 项。`

## 用户反馈

用户端不提供 DBText 标注、反馈包导出、训练、导入样本或模型替换功能。

如果用户发现 DBText 乱码未修复或误修，处理方式是直接联系开发者，并发送：

- 问题 DWG；
- 必要截图；
- 现象描述；
- AutoCAD 版本和 AFR 版本。

开发者在私有环境中提取样本、人工审核、训练 LightGBM / XGBoost / FastTree 模型，并随新版 DLL 发布。

## 边界

- 不要恢复 `data/DbTextRepairModel.jsonl` 作为运行时输入。
- 不要恢复 `AFRDBTEXTLABEL`、本地训练、JSONL 合并或用户端模型替换入口。
- 不要把 DBText 编码修复做成全局字符串替换。
- 字形缺笔、SHX 字体形状问题仍应走字体/样式诊断，不应强行写回 DBText 文本。

## 提交前检查

- Release DLL 不包含 `DbTextRepairModel.jsonl`。
- Release DLL 不包含 `AFRDBTEXTLABEL`、`AFRDBTEXTBATCHTRAIN`、`AFRGLYPHCOREEXPORT` 或 `AFRGLYPHCOREEXPORTSELECT`。
- 无官方 ONNX 模型时 DBText 修复安全跳过。
- 有官方 ONNX 模型时仅高置信候选允许写回。
- 无疑似异常图纸不应加载文枢模型，也不应输出 DBText 文枢提示。
- `AFRLOG` 仍只展示字体替换结果，不作为 DBText AI 纠错入口。
