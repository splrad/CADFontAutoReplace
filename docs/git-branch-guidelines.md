# Git 分支与 PR 规则

本文面向普通贡献者，说明如何命名分支、提交改动和打开 PR。

## 基本原则

- 从 `main` 创建短期分支。
- 一个分支只处理一个问题，或一组紧密相关的问题。
- 不直接向 `main` 推送。
- 不把格式化、重命名、重构和功能修复混在同一个 PR 里。

## 分支命名

分支名使用小写英文、数字和连字符。推荐格式如下：

| 类型 | 格式 | 示例 |
| --- | --- | --- |
| 功能 | `feature/<topic>` | `feature/add-ttf-support` |
| 修复 | `bugfix/<topic>` | `bugfix/mtext-crash` |
| 文档 | `docs/<topic>` | `docs/developer-guide` |
| 重构 | `refactor/<topic>` | `refactor/font-index` |
| 性能 | `perf/<topic>` | `perf/font-scan-cache` |
| 构建或维护 | `chore/<topic>` | `chore/build-warning` |

如果改动对应 Issue，可以把编号放进分支名，例如 `bugfix/issue-123`。

## 本地流程

先 Fork 仓库，然后 clone 自己的 Fork：

```powershell
git clone https://github.com/<your-name>/CADFontAutoReplace.git
cd CADFontAutoReplace
git remote add upstream https://github.com/splrad/CADFontAutoReplace.git
git fetch upstream
git checkout -b docs/developer-guide upstream/main
```

完成修改后：

```powershell
git status
git diff
git add <changed-files>
git commit -s -m "docs: improve developer guide"
git push -u origin docs/developer-guide
```

如果你使用 Fork，从 GitHub 页面打开指向上游仓库 `main` 分支的 PR。

## PR 要求

推送到本仓库短期分支后，PR 标题和说明会自动生成或更新。打开 PR 后先检查生成内容是否准确，再按需要补充人工信息，重点是：

- 自动摘要是否漏掉关键改动。
- 验证命令是否完整。
- 哪些内容没有验证，原因是什么。
- 是否有审阅者需要特别关注的兼容性或运行环境限制。

如果从 Fork 打开的 PR 没有自动生成摘要，按以上要点手动补充即可。

文档改动至少运行：

```powershell
git diff --check
```

代码改动按范围运行对应构建。常见命令见 [开发者指南](developer-guide.md)。

## 目标分支

贡献者通常向 `main` 分支提交 PR。维护者会根据仓库规则处理后续审查、验证和发布。

如果你不确定目标分支，优先选择 `main`；自动生成的 PR 说明不清楚时，补充一句改动目的。

维护者需要本地复测 PR 时，会直接拉取 PR head 或 PR merge ref，例如 `gh pr checkout <PR号>`，或 `git fetch upstream refs/pull/<PR号>/merge:review/pr-<PR号>-merge`。

## 提交建议

提交信息用简短动宾结构说明意图，例如：

```text
docs: improve netload setup guide
fix: handle missing bigfont fallback
refactor: isolate shx availability lookup
```

仓库会自动生成 PR 标题，但提交信息仍应保持可读。一个 PR 可以包含多个提交，每个提交都应保持可解释。

推荐使用 DCO 风格的 `Signed-off-by` 提交，例如 `git commit -s -m "docs: improve developer guide"`。这会在提交信息末尾加入：

```text
Signed-off-by: Your Name <your-email@example.com>
```

仓库会运行非阻断的 `DCO Sign-off Advisory` 检查，提示缺少或邮箱不匹配的 `Signed-off-by`。它当前不会阻止合并，但有助于未来接收更多外部贡献，或把本项目流程复用到其它仓库。

如果已经提交后需要补签：

```powershell
git fetch upstream
git commit --amend -s
git rebase --signoff upstream/main
```

## 不应提交的内容

- 本地日志、截图、临时文件、浏览器 profile。
- 构建产物和发布包。
- 未脱敏的 DWG、客户数据或路径截图。
- 个人环境配置、访问令牌、密钥。
- 与当前 PR 无关的大范围格式化。

## 等待 Review 时

- 保持分支可构建。
- 对 review 逐条回应，已处理的问题可说明对应提交。
- 如果发现 PR 范围过大，优先拆分成更小的 PR。
- 如果某个运行时行为无法本机验证，在 PR 中明确写出缺少的环境，例如没有 AutoCAD 2027。
