# DBText 模型修复与人工确认

本文档描述当前代码中的 DBText 单行文字修复链路。旧的 `AFRDBTEXTPROBE`、`AFRTRACER*`、`DwgFilerCodePageScopeHook`、`TextEditorDbcsDecodeHook` 与 `CodePageFamilyHook` 调查文档已删除，因为这些命令和 Hook 类不在当前代码中。

## 当前实现

当前 DBText 修复是 Release 可用的模型数据集流程，不依赖原生 code page Hook。

执行入口位于 `ExecutionController.Execute`：

1. 检测并替换文字样式表中的缺失字体。
2. 执行 `DbTextRepairService.Repair`，扫描非外参块中的 `DBText`。
3. 扫描并处理 MText 内联字体。
4. 输出命令行摘要；若仍有未修复 DBText，会提示执行 `AFRDBTEXTLABEL`。

## 数据位置

- 仓库内置数据集：`data/DbTextRepairModel.jsonl`。
- 嵌入资源名：`AFR.DbTextRepairModel.jsonl`。
- 本地开发时，若运行目录可向上找到仓库根目录，活动模型优先使用仓库 `data/`。
- 正式安装时，部署工具会把嵌入数据集合并到 `%APPDATA%\CADFontAutoReplace\DbTextRepairModel.jsonl`。
- 临时导入文件使用 `DbTextRepairModel*.jsonl`，合并成功后会删除；损坏文件会重命名为 `.corrupt.*.jsonl`。

## 自动修复规则

`DbTextRepairCandidateGenerator` 只生成确定性候选：

- 当前文本自身，用作 no-op 候选。
- `big5-carrier-to-gbk` 候选。
- 历史人工标签候选。

`DbTextRepairAdvisor` 会读取标签、冲突记录和神经排序参数。自动写回仍受标签策略约束：只有对象上下文与历史人工 `repair` 标签精确匹配，且候选文本与当前文本不同，才会写回 `DBText.TextString`。冲突、`keep`、`glyph-issue` 或无精确标签时不自动修改图纸。

神经模型用于排序和辅助判断，不单独作为写回证据。

## 人工确认命令

命令：`AFRDBTEXTLABEL`

使用流程：

1. 在 AutoCAD 中完整输入 `AFRDBTEXTLABEL`。
2. 选择一个 `DBText` 单行文字对象。
3. 窗口会显示当前文本、候选文本、候选来源、AI 分数和对象上下文。
4. 选择：
   - 写回正确文本；
   - 保持当前文本；
   - 标记为字体/字形问题。

确认后会追加一条标签记录，并触发模型合并与本地神经参数刷新。若选择写回正确文本，命令会立即修改当前对象并刷新显示。

## 边界

- 不要把字形缺笔、SHX 字体形状问题当作 DBText 编码修复；这类问题应标记为 `glyph-issue` 并走字体/样式诊断。
- 不要恢复旧的 code page 探针命令作为公开使用文档；当前公开维护入口是 `AFRDBTEXTLABEL`。
- 不要把 `big5-carrier-to-gbk` 候选扩展成全局字典替换。它只是候选来源，写回必须由精确标签或后续明确策略授权。
- 更新 DBText 模型字段时，需同步 `DbTextRepairModelRecords`、`DbTextRepairModelJsonl`、部署器嵌入资源和本文档。

## 提交前检查

- `AFRDBTEXTLABEL` 能选择 DBText 并写入标签。
- `data/DbTextRepairModel.jsonl` 可被插件与部署器嵌入。
- 安装流程能将模型合并到 `%APPDATA%\CADFontAutoReplace`。
- `AFRLOG` 仍只展示字体替换结果，不作为 DBText 模型日志查看器。
