# PR governance webhook relay

这个 Cloudflare Worker 为 PR 治理补充两个 GitHub Actions 之外的事件入口：

- 将 `pull_request_review_thread` 的 `resolved`（以及未来可能收到的 `unresolved`）转换为一次 `repository_dispatch`。
- 对严格白名单内、由默认分支可信 `workflow_run` 事件触发且仍为 `action_required` 的 Matrix run 执行一次审批。

两条路径都先验证 GitHub App webhook 签名、repository、installation 和事件字段，不等待后续 workflow，也不轮询状态。

## 首次部署

1. 配置 Actions secrets：`CLOUDFLARE_API_TOKEN`、`CLOUDFLARE_ACCOUNT_ID`。API token 需要目标账号的 Workers Scripts Edit 权限。
2. 在 Worker 中用 `wrangler secret put` 分别配置 `GITHUB_WEBHOOK_SECRET`、`GITHUB_APP_ID`、`GITHUB_APP_PRIVATE_KEY`。代码部署不会覆盖这些 secrets。
3. 运行 `Deploy Webhook Relay` 的 `workflow_dispatch` 完成首次部署；Wrangler migration 会创建 delivery coordinator Durable Object。
4. 将 Worker URL 配置为 GitHub App webhook URL，启用 SSL verification，使用与 `GITHUB_WEBHOOK_SECRET` 相同的高熵 secret，并订阅 Pull request review thread 与 Workflow run 事件。

GitHub App 需要 Pull requests read、Contents write 和 Actions write。Worker 根据 webhook installation ID 创建仅限当前 repository 的短期 installation token，并按操作只申请 Contents write 或 Actions write。

`TARGET_REPOSITORY` 限定当前单仓库部署；`APPROVABLE_WORKFLOW_PATHS` 是逗号分隔的可信 workflow 路径白名单，当前只允许 `.github/workflows/pr-validation-matrix.yml`。共享 Steward Relay 迁移后，这两个部署参数将由默认分支 Manifest 和共享协议替代。

## 本地验证

```text
npm ci
npm test
npm run typecheck
```

Durable Object 以 `repository_id:X-GitHub-Delivery` 做强一致 claim。处理中的 claim 使用 60 秒短租约，租约内重试返回可重试错误，过期后允许接管；明确的 dispatch、查询或审批失败会立即释放 claim，只有操作成功才记录并保留 24 小时。Worker 不记录 webhook 正文、签名、App 私钥或 installation token。
