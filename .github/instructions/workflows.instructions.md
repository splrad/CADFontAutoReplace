---
applyTo: ".github/workflows/**"
---

所有 GitHub Copilot Code Review 输出必须使用简体中文。只保留 workflow 关键字、权限名、action 名称、命令和文件路径为英文。

每条 review comment 第一行必须是 `严重程度：阻断` 或 `严重程度：建议`。

审查 GitHub Actions workflow 时，优先检查：

- `pull_request_target`、`workflow_run`、`issue_comment` 等高风险触发器是否读取或执行了不可信 PR head 内容。
- `permissions` 是否最小化，是否把 `contents: write`、`pull-requests: write`、`issues: write`、`security-events: write` 限定在真正需要的 job。
- required check 的 job `name` 是否被改动；本仓库 ruleset 依赖这些名称。
- GitHub App token、`GITHUB_TOKEN`、secrets 和 artifact 是否跨信任边界使用。
- 自动建 PR、门禁、Release workflow 的触发链是否会造成循环触发、重复 PR 或绕过审批。

只有会造成权限扩大、绕过门禁、发布错误、泄露 secret、执行不可信代码或破坏 required check 的问题，才标记为 `严重程度：阻断`。
