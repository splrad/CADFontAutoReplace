---
applyTo: ".github/workflows/**"
---

GitHub Copilot Code Review 输出的主体语言必须是简体中文。为了保留技术含义，可以使用英文 workflow 关键字、权限名、action 名称、job 名称、required check、命令、文件路径和难以准确翻译的专有名词。

每条 review comment 第一行必须提供严重程度标记，优先使用 `严重程度：阻断` 或 `严重程度：建议`，也兼容 `Severity: blocking` 或 `Severity: suggestion`。

审查 GitHub Actions workflow 时，优先检查：

- `pull_request_target`、`workflow_run`、`issue_comment` 等高风险触发器是否读取或执行了不可信 PR head 内容。
- `permissions` 是否最小化，是否把 `contents: write`、`pull-requests: write`、`issues: write`、`security-events: write` 限定在真正需要的 job。
- CodeQL 使用 GitHub Advanced Security default setup，并由 main ruleset 的 `Require code scanning results` 管理；本仓库不再维护自定义 CodeQL workflow，也不把 CodeQL job name 作为 required status check。
- `Main Authorization Gate`、`Copilot Review Gate` 的 job `name` 是否被改动；本仓库 ruleset 依赖这两个 required check 名称。
- `DCO Sign-off Advisory` 是非阻断提示 workflow，不应加入 required checks，也不应因缺少 Signed-off-by 失败。
- GitHub App token、`GITHUB_TOKEN`、secrets 和 artifact 是否跨信任边界使用。
- 自动建 PR、门禁、Release workflow 的触发链是否会造成循环触发、重复 PR 或绕过审批。

只有会造成权限扩大、绕过门禁、发布错误、泄露 secret、执行不可信代码或破坏 required check 的问题，才标记为 `严重程度：阻断`。
