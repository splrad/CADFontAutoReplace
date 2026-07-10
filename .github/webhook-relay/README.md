# PR review thread webhook relay

GitHub Actions 没有 `pull_request_review_thread` 原生触发器。这个 Cloudflare Worker 验证 GitHub App webhook 签名，并把开放默认分支 PR 的 `resolved`（以及未来可能收到的 `unresolved`）事件转换为一次 `repository_dispatch`。

## 首次部署

1. 配置 Actions secrets：`CLOUDFLARE_API_TOKEN`、`CLOUDFLARE_ACCOUNT_ID`。API token 需要目标账号的 Workers Scripts Edit 权限。
2. 在 Worker 中用 `wrangler secret put` 分别配置 `GITHUB_WEBHOOK_SECRET`、`GITHUB_APP_ID`、`GITHUB_APP_PRIVATE_KEY`。代码部署不会覆盖这些 secrets。
3. 运行 `Deploy Webhook Relay` 的 `workflow_dispatch` 完成首次部署；Wrangler migration 会创建 delivery coordinator Durable Object。
4. 将 Worker URL 配置为 GitHub App webhook URL，启用 SSL verification，使用与 `GITHUB_WEBHOOK_SECRET` 相同的高熵 secret，并订阅 Pull request review thread 事件。

GitHub App 需要 Pull requests read 权限来接收该事件，以及 Contents write 权限来发送 `repository_dispatch`。Worker 根据 webhook installation ID 创建仅限当前 repository、仅含 Contents write 的短期 installation token。

## 本地验证

```text
npm ci
npm test
npm run typecheck
```

Durable Object 以 `repository_id:X-GitHub-Delivery` 做强一致 claim；明确的 dispatch 失败会释放 claim，成功记录保留 24 小时后由 alarm 清理。Worker 不记录 webhook 正文、签名、App 私钥或 installation token。
