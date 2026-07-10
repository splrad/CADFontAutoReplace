# PR review thread webhook relay

GitHub Actions 没有 `pull_request_review_thread` 原生触发器。这个 Cloudflare Worker 验证 GitHub App webhook 签名，并把开放 `main` PR 的 `resolved`（以及未来可能收到的 `unresolved`）事件转换为一次 `repository_dispatch`。

## 首次部署

1. 创建 Cloudflare KV namespace，把 32 位 namespace ID 保存为仓库变量 `CLOUDFLARE_WEBHOOK_DELIVERIES_KV_ID`。
2. 配置 Actions secrets：`CLOUDFLARE_API_TOKEN`、`CLOUDFLARE_ACCOUNT_ID`。API token 至少需要目标账号的 Workers Scripts Edit 和 Workers KV Storage Edit 权限。
3. 在 Worker 中用 `wrangler secret put` 分别配置 `GITHUB_WEBHOOK_SECRET`、`GITHUB_APP_ID`、`GITHUB_APP_PRIVATE_KEY`。代码部署不会覆盖这些 secrets。
4. 运行 `Deploy Webhook Relay` 的 `workflow_dispatch` 完成首次部署。
5. 将 Worker URL 配置为 GitHub App webhook URL，启用 SSL verification，使用与 `GITHUB_WEBHOOK_SECRET` 相同的高熵 secret，并订阅 Pull request review thread 事件。

GitHub App 需要 Pull requests read 权限来接收该事件，以及 Contents write 权限来发送 `repository_dispatch`。Worker 根据 webhook installation ID 创建仅限当前 repository、仅含 Contents write 的短期 installation token。

## 本地验证

```text
npm ci
npm test
npm run typecheck
```

KV 只在 GitHub dispatch 成功后记录 `X-GitHub-Delivery`，TTL 为 24 小时。Worker 不记录 webhook 正文、签名、App 私钥或 installation token。
