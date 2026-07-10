# PR governance webhook relay

这个 Cloudflare Worker 为 PR 治理补充 GitHub Actions 之外的 review 状态入口：

- 将 `pull_request_review`、`pull_request_review_comment` 和
  `pull_request_review_thread` 转换为 `pr-review-state-changed` repository dispatch。
- GitHub Actions 原生 review workflow 不再接收 Copilot bot 事件，避免无法通过 API
  恢复的同仓库 `action_required` run。

所有路径都先验证 GitHub App webhook 签名、repository、installation、开放 PR、
默认分支、当前 head 和事件 action，不等待后续 workflow，也不轮询状态。

## 首次部署

1. 配置 Actions secrets：`CLOUDFLARE_API_TOKEN`、`CLOUDFLARE_ACCOUNT_ID`。API token 需要目标账号的 Workers Scripts Edit 权限。
2. 在 Worker 中用 `wrangler secret put` 分别配置 `GITHUB_WEBHOOK_SECRET`、`GITHUB_APP_ID`、`GITHUB_APP_PRIVATE_KEY`。代码部署不会覆盖这些 secrets。
3. 运行 `Deploy Webhook Relay` 的 `workflow_dispatch` 完成首次部署；Wrangler migration 会创建 delivery coordinator Durable Object。
4. 将 Worker URL 配置为 GitHub App webhook URL，启用 SSL verification，使用与
   `GITHUB_WEBHOOK_SECRET` 相同的高熵 secret，并订阅 Pull request review、
   Pull request review comment 与 Pull request review thread 事件。

GitHub App 需要 Pull requests read 和 Contents write。Worker 根据 webhook installation ID
创建仅限当前 repository、Contents write 的短期 installation token，用于发送 repository dispatch。

`TARGET_REPOSITORY` 限定当前单仓库部署。共享 Steward Relay 迁移后，该参数将由默认分支 Manifest 和共享协议替代。

## 从旧 Relay 升级

升级顺序不可颠倒：

1. 先在 GitHub App 的 Permissions & events 中订阅 Pull request review、
   Pull request review comment 和 Pull request review thread，并保留旧 Workflow run 订阅。
2. 再合并包含新 Matrix、PR Review Signal 和 Worker 的提交；等待 main 上
   `Deploy Webhook Relay` 完成。不能在订阅到位前移除旧 review workflow 触发器。
3. 用测试 PR 分别产生 review、inline comment 和 resolved conversation，确认每次 delivery
   都生成 `pr-review-state-changed`，且授权与 Copilot 两个治理目标均重新读取当前状态。
4. 验证完成后取消 GitHub App 的 Workflow run 订阅，并确认 Cloudflare production
   不再包含旧 `APPROVABLE_WORKFLOW_PATHS` plaintext variable。

## 本地验证

```text
npm ci
npm test
npm run typecheck
```

Durable Object 以 `repository_id:X-GitHub-Delivery` 做强一致 claim。处理中的 claim
使用 60 秒短租约，租约内重试返回可重试错误，过期后允许接管；明确的 dispatch
失败会立即释放 claim，只有操作成功才记录并保留 24 小时。Worker 不记录 webhook
正文、签名、App 私钥或 installation token。
