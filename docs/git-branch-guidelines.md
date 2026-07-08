---
日期: 2026-04-19
tags:
  - Git
  - GitHub
---
# Git 分支管理摘要

## 1. 核心分支

本仓库采用精简的分支模型，主要包含以下长期分支：

| 分支 | 用途 | 权限 |
| --- | --- | --- |
| `main` | 生产环境的主干分支，包含所有已发布的稳定代码。| 受保护，**禁止直接推送**。默认仅允许通过 `test` 分支的 PR 合并；核心开发者可走快速通道。 |
| `test` | 集成测试分支，用于汇聚各个功能分支并进行自动化测试和回归验证。 | 受保护。允许合并来自于功能分支的 PR。 |

## 2. 短期分支命名规范

日常开发中，必须从 `main` 切出短期分支。分支命名应清晰表达工作性质：

- **功能开发（Feature）**
  - 格式：`feature/<功能简称或单号>`
  - 示例：`feature/add-ttf-support`，`feature/issue-123`

- **缺陷修复（Bugfix）**
  - 格式：`bugfix/<问题简称或单号>`
  - 示例：`bugfix/fix-mtext-crash`，`bugfix/issue-456`

- **文档更新（Docs）**
  - 格式：`docs/<更新内容>`
  - 示例：`docs/update-readme`

- **性能优化 / 重构（Refactor / Perf）**
  - 格式：`refactor/<重构内容>` 或 `perf/<优化内容>`
  - 示例：`refactor/extract-hook-layer`

## 3. 标准开发流程

1. **同步主干**：`git checkout main` -> `git pull`
2. **创建分支**：`git checkout -b feature/my-new-feature`
3. **本地提交**：完成代码编写与本地测试。
4. **推送到远端**：`git push origin feature/my-new-feature`
5. **自动创建 PR**：推送分支后由 `PR Automation` 自动创建或更新合并请求。
6. **自动化流转**：
   - 非核心开发者：自动创建/更新 `feature -> test` 的 PR。
   - 核心开发者：自动创建/更新 `feature -> main` 的快速通道 PR。
   - `test` 验证通过并合并后，流程自动创建/更新 `test -> main` 的 PR。
7. **合并门禁**：`PR Governance` 保留核心开发者自动审批、受保护配置审查、`main` 来源限制，并要求 `main` 目标 PR 完成 Copilot 代码审查且无未解决重大问题。

## 4. 必需状态检查

重构后的分支保护规则建议使用以下状态检查：

- `test`：`PR Governance / Protected Configuration Gate`
- `main`：`PR Governance / Main Authorization Gate`
- `main`：`PR Governance / Source Branch Gate`
- `main`：`PR Governance / Copilot Review Gate`

## 5. 工作流迁移顺序

1. 创建并安装自动化 GitHub App，建议命名为 `Workflow Automation`；配置仓库变量 `WORKFLOW_AUTOMATION_APP_CLIENT_ID` 和仓库密钥 `WORKFLOW_AUTOMATION_APP_PRIVATE_KEY`。
2. 确认 App 至少具备 `Contents: Read and write`、`Pull requests: Read and write`、`Issues: Read and write` 仓库权限；若 `test` 受 ruleset 保护，将该 App 加入允许同步 `test` 的 bypass 规则。
3. 合并工作流重构 PR，让新的 workflow 文件进入默认分支。
4. 使用临时 PR 触发新的 `PR Governance` 检查，确认 GitHub UI 中已经出现上述 4 个状态检查名称。
5. 将 ruleset / branch protection 的 required checks 切换到上述新名称。
6. 确认新门禁生效后关闭临时 PR。

# 分支结构
```
main                     # 主分支（稳定 / 可发布）
test                     # 测试分支（集成验证）
feature/<功能>           # 新功能
bugfix/<问题>            # BUG问题
refactor/<重构>          # 重构
perf/<优化>              # 性能优化
chore/<杂项>             # 配置/依赖/构建等
docs/<文档>              # 文档
```
# 核心原则
- main 永远稳定（随时可用）
- test 用来“过渡验证”
- 所有开发都从 main 拉分支
- PR 标题与说明必须来自代码差异，不能只使用来源分支和目标分支
- 临时分支**用完就删**
# 分支模板
## ✅ 功能开发（feature/）

```
feature/core              # 核心功能  
feature/module            # 模块功能  
feature/api               # 接口功能  
feature/ui                # 界面功能  
feature/config            # 配置功能  
feature/plugin            # 插件功能  
feature/integration       # 集成功能  
feature/extension         # 扩展功能
```
👉 万能写法：`feature/module`
## 🐞 问题修复（bugfix/）

```
bugfix/core               # 核心问题  
bugfix/api                # 接口问题  
bugfix/ui                 # 界面问题  
bugfix/config             # 配置问题  
bugfix/logic              # 逻辑错误  
bugfix/crash              # 崩溃问题  
bugfix/performance        # 性能问题
```
👉 万能写法：`bugfix/issue`
## 🔧 重构（refactor/）
```
refactor/core             # 核心重构  
refactor/module           # 模块重构  
refactor/structure        # 结构调整  
refactor/code             # 代码优化（万能）  
refactor/cleanup          # 清理代码
```
👉 万能写法：`refactor/code`
## 🚀 性能优化（perf/）

```
perf/core                 # 核心优化  
perf/module               # 模块优化  
perf/api                  # 接口优化  
perf/ui                   # 界面优化  
```
👉 万能写法：`perf/optimization`

## 📄 文档（docs/）

```
docs/readme               # README  
docs/api                  # 接口文档  
docs/guide                # 使用说明  
docs/setup                # 安装文档  
docs/comment              # 注释补充
```
👉 万能写法：`docs/readme`
## 🔧 杂项（chore/）

```
chore/config              # 配置调整  
chore/deps                # 依赖更新  
chore/build               # 构建相关  
chore/ci                  # CI/CD  
chore/setup               # 初始化/环境  
chore/cleanup             # 清理杂项
```
👉 万能写法：`chore/misc`
