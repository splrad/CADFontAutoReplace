---
applyTo: ".github/scripts/**,.github/actions/**,.github/labeler.yml,.github/dependabot.yml,.github/release.yml,.github/copilot-instructions.md,.github/instructions/**,.github/pull_request_template.md"
---

GitHub Copilot Code Review 输出的主体语言必须是简体中文。为了保留技术含义，可以使用英文代码标识符、文件路径、GitHub API 路径、workflow/job/action 名称、required check、label 名称、状态码和难以准确翻译的专有名词。

每条 review comment 第一行必须提供严重程度标记，优先使用 `严重程度：阻断` 或 `严重程度：建议`，也兼容 `Severity: blocking` 或 `Severity: suggestion`。

审查仓库自动化脚本和配置时，优先检查：

- PR 识别是否会把错误 head/base、旧提交、错误 bot 或无关 PR 当作当前 PR。
- Copilot Code Review 识别必须只认真实 Copilot reviewer，不得混入 Codex/ChatGPT Connector 或修复任务 bot。
- 标签推断必须区分 PR 管理标签和 Release Notes 标签，workflow/docs/chore 不能污染发布说明。
- 评论 upsert marker 不得改变，避免重复评论或覆盖人工内容。
- GitHub API 分页、`--paginate --slurp`、空响应和 bot login 归一化是否正确。
- Copilot CLI prompt 和兜底摘要主体必须输出简体中文，但不要翻译代码符号、路径、label 和 API 名称。

只有会造成门禁误通过、误阻断、错误创建 PR、错误发布、错误标签污染发布说明或破坏评论 marker 的问题，才标记为 `严重程度：阻断`。
