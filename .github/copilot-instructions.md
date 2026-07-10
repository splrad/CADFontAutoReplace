第一优先级：GitHub Copilot Code Review 输出的主体语言必须是简体中文，包括 pull request overview、inline review comment、建议说明和总结。

为了保留原始技术含义，允许使用英文技术名词、API 名称、workflow/job/action 名称、required check、label 名称、文件路径、命令、状态码、代码标识符和无法准确翻译的专有名词。

重点审查具体 correctness、安全、workflow、release、兼容性和发布包风险。

每条 review comment 正文第一行必须提供机器可解析严重程度标记。优先使用中文标记，也兼容英文标记：

- `严重程度：阻断` / `Severity: blocking`：必须先修复才能合并的问题。
- `严重程度：建议` / `Severity: suggestion`：不触发 Copilot Code Review Gate 失败的可选改进；inline conversation 仍需按 ruleset 标记为 Resolved。

每条 inline review comment 的第二行必须提供机器可解析的简短标题，优先使用中文标记，也兼容英文标记：

- `标题：GH_EXECUTABLE_ARGS 解析缺少异常处理`
- `Title: GH_EXECUTABLE_ARGS parsing lacks error handling`

标题必须具体概括当前问题，不超过 60 个字符，不重复严重程度，不使用 Markdown 链接，不以句号结尾。第二行后空一行，再输出问题说明和建议。

只有 bug、安全问题、CI/CD 行为损坏、Release/发布包回归、数据丢失风险，或破坏本仓库分支与审批规则的变更，才使用 `严重程度：阻断`。

普通重构、命名偏好、格式问题或主观可读性建议不得标记为阻断。

当没有发现需要阻断合并的问题时，优先在 review 正文末尾使用以下 Markdown 结论块，供仓库 workflow 判定；标题必须独立成行：

```markdown
## 结论

未发现需要阻断合并的问题。
```

为兼容历史记录，仓库 workflow 仍接受旧格式 `结论：未发现需要阻断合并的问题。`。

如果 GitHub Copilot 官方流程在无问题时自动输出 `Copilot reviewed X out of Y changed files in this pull request and generated no comments.` 或 `Copilot reviewed X out of Y changed files in this pull request and generated no new comments.`，这些英文模板同样是仓库 workflow 接受的无阻断信号；官方模板不受本说明强制改写。

如果当前 head 的官方 review 正文明确记录曾生成评论，仓库 workflow 会在这些 conversation 全部解决后将其视为有效恢复信号。

CodeQL 使用 GitHub Advanced Security default setup，并由 main ruleset 的 `Require code scanning results` 管理；本仓库不再维护自定义 CodeQL workflow，也不把 CodeQL job name 作为 required status check。

Copilot Code Review Gate 只解析当前 head 的未解决当前线程；`isOutdated` 线程属于旧 head 上下文，由最新 Copilot review 和 ruleset 的 review-thread-resolution 规则共同兜底。

严重程度是本仓库自定义约定，不是 GitHub Copilot 官方结构化字段，也不依赖 GitHub 页面上的 Low/Medium 等 UI 徽标；请按上述文本输出，供仓库 workflow 解析。
