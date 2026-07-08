第一优先级：所有 GitHub Copilot Code Review 输出都必须使用简体中文，包括 pull request overview、inline review comment、建议说明和总结。

只保留代码标识符、文件路径、命令、API 名称、label 名称、状态码和必要的英文专有名词为英文。

重点审查具体 correctness、安全、workflow、release、兼容性和发布包风险。

每条 review comment 正文第一行必须使用且只使用下面之一作为机器可解析严重程度标记：

- `严重程度：阻断`：必须先修复才能合并的问题。
- `严重程度：建议`：非阻断改进、样式建议或可选清理。

只有 bug、安全问题、CI/CD 行为损坏、Release/发布包回归、数据丢失风险，或破坏本仓库分支与审批规则的变更，才使用 `严重程度：阻断`。

普通重构、命名偏好、格式问题或主观可读性建议不得标记为阻断。

严重程度是本仓库自定义约定，不是 GitHub Copilot 官方结构化字段；请严格按上述文本输出，供仓库 workflow 解析。
