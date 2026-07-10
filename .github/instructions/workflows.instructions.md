---
applyTo: ".github/workflows/**"
---

输出语言、严重程度和标题格式统一遵循 `.github/copilot-instructions.md`；本文件只补充 workflow 路径的审查重点。

审查 GitHub Actions workflow 时，优先检查：

- `pull_request_target`、`workflow_run`、`issue_comment` 等高风险触发器是否读取或执行了不可信 PR head 内容。
- `permissions` 是否最小化，是否把 `contents: write`、`pull-requests: write`、`issues: write`、`security-events: write` 限定在真正需要的 job。
- CodeQL 使用 GitHub Advanced Security default setup，并由 main ruleset 的 `Require code scanning results` 管理；本仓库不再维护自定义 CodeQL workflow，也不把 CodeQL job name 作为 required status check。
- main ruleset 只应把 `PR Validation Matrix Gate` 配置为集中 required status check，source 必须匹配创建 check run 的 GitHub App 或 Any source；`Main Authorization Gate` 和 `Copilot Code Review Gate` 保留为矩阵目标，不应再单独设为 required status check。
- `PR Validation Matrix` 是唯一拥有 `Actions: write` 的编排入口；conversation resolved 由 GitHub App webhook relay 转为 `repository_dispatch`，不得使用 cron、sleep、截止时间循环或状态轮询刷新门禁。
- `review_requested`、`pull_request_review` / `pull_request_review_comment` 必须先经过无密钥 `PR Review Signal`，再由验证矩阵通过 `workflow_run` 调度治理，避免 fork PR 的 review 事件直接读取仓库 secrets。
- `DCO Sign-off Advisory` 是非阻断提示 workflow，不应加入 required checks，也不应因缺少 Signed-off-by 失败。
- GitHub App token、`GITHUB_TOKEN`、secrets 和 artifact 是否跨信任边界使用。
- 自动建 PR、门禁、Release workflow 的触发链是否会造成循环触发、重复 PR 或绕过审批。

只有会造成权限扩大、绕过门禁、发布错误、泄露 secret、执行不可信代码或破坏 required check 的问题，才标记为 `严重程度：阻断`。
