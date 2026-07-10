---
applyTo: ".github/scripts/**,.github/actions/**,.github/pr-classification-rules.json,.github/dependabot.yml,.github/release.yml,.github/copilot-instructions.md,.github/instructions/**,.github/pull_request_template.md"
---

输出语言、严重程度和标题格式统一遵循 `.github/copilot-instructions.md`；本文件只补充自动化脚本和配置路径的审查重点。

审查仓库自动化脚本和配置时，优先检查：

- PR 识别是否会把错误 head/base、旧提交、错误 bot 或无关 PR 当作当前 PR。
- Copilot Code Review 识别必须只认真实 Copilot reviewer，不得混入 Codex/ChatGPT Connector 或修复任务 bot。
- Copilot Code Review Gate 只解析当前 head 的未解决当前线程并完整分页 thread comments；`isOutdated` 线程属于旧 head 上下文，由最新 Copilot review 和 ruleset 的 review-thread-resolution 规则共同兜底。
- 当前 head 的 Copilot review 如果明确记录曾生成评论，且相关 conversation 已全部解决，应作为有效恢复信号；未知 review 模板仍失败关闭。
- 无法关联 GitHub login 的非机器人 commit author 必须失败关闭，获得核心开发者对当前 head 的手动 approval 后才能通过。
- 标签推断必须区分隐藏 PR 分类元数据、可见 PR 标签和 Release Notes 标签，workflow/docs/chore 不能污染发布说明。
- PR / Release 分类规则必须统一维护在 `.github/pr-classification-rules.json`；不要在 JS 或 PowerShell 脚本里复制 runtime 路径、安装包路径或 release category 判定。
- DCO Sign-off Advisory 是非阻断提示；脚本可以 upsert/delete marker comment，但不得因缺少 Signed-off-by 让 job 失败。
- 评论 upsert marker 不得改变，避免重复评论或覆盖人工内容。
- GitHub API 分页、`--paginate --slurp`、空响应和 bot login 归一化是否正确。
- 治理脚本不得用 sleep、截止时间循环、定时重试或轮询等待异步状态；一次事件最多执行一次补跑并立即结束。
- Copilot CLI prompt 和兜底摘要主体必须输出简体中文，但不要翻译代码符号、路径、label 和 API 名称。

只有会造成门禁误通过、误阻断、错误创建 PR、错误发布、错误标签污染发布说明或破坏评论 marker 的问题，才标记为 `严重程度：阻断`。
