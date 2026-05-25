# AFR 开发者指南

> 适合会基本 C#、.NET 和 Git，但第一次参与本仓库或第一次维护 AutoCAD 插件的人。本文先带你跑通一次开发闭环，再说明维护边界。

## 1. 先记住这 5 条

1. 最终产物是按 AutoCAD 年份区分的插件 DLL，例如 `AFR-ACAD2026.dll`。
2. 常用开发入口是版本壳 `src/AutoCAD/AFR-ACAD20XX/`，真正业务代码主要在 `src/AutoCAD/AFR.AutoCAD/`。
3. `AFR.Core`、`AFR.UI`、`AFR.HostIntegration`、`AFR.Polyfills` 不能引用 AutoCAD SDK。
4. Hook 安装成功不等于字体映射成功；成功必须看真实 `HookHandler` hit / redirect 和 `FontRuntimeMappingStore` 结果。
5. 改了命令、Hook、发布或范围边界时，要同步 README、`PROJECT_HANDOVER.md`、`.github/copilot-instructions.md` 或本文中的对应事实。

当前项目只维护 AutoCAD 缺失字体自动替换、文件级字体运行时映射和部署器发布。当前源码不包含 DBText、GlyphCore、WenShu、AI 推理、训练数据、候选包、模型、报告、补绘或 native decode evidence 链路；这些名称只作为历史上下文，不能当作当前入口恢复。

## 2. 30 分钟上手路线

按这个顺序做，目标是先确认你能构建、加载、执行命令和看日志。

1. 打开仓库根目录的 `CADFontAutoReplace.slnx`。
2. 选择你本机实际安装的 AutoCAD 年份，例如 2026 对应 `src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj`。
3. 在仓库根目录构建目标版本壳：

```powershell
dotnet build src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj
```

4. 找到输出 DLL：`artifacts/bin/AFR-ACAD2026/debug/AFR-ACAD2026.dll`。
5. 启动对应 AutoCAD，用 VS 调试版本壳，或在 CAD 里手动 `NETLOAD` 这个 DLL。
6. CAD 命令行执行 `AFR`，确认字体配置窗口能打开。
7. 打开一张测试 DWG，执行 `AFRLOG`，确认能看到样式表检测和替换结果。
8. Debug 构建下执行 `AFRVIEW`，选择 MText 或 MLeader，确认诊断窗口能打开。
9. 到插件输出或部署目录查看 `AFR_Diag_*.jsonl`，先按 `seq` 看启动和执行顺序。
10. 做任何改动前，先确认 CAD 当前加载的 DLL 就是你刚构建出来的 DLL。

如果你没有对应 AutoCAD 环境，也可以先完成构建、文档、部署器或非 CAD 层改动，但不要声称运行时 Hook 行为已经验证。

## 3. 第一次构建

仓库支持 AutoCAD 2018-2027，每个年份有一个版本壳工程。`20XX` 要替换成你的目标版本。

```powershell
# 构建一个版本插件
dotnet build src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj

# 构建部署器
dotnet build src/AFR.Deployer/AFR.Deployer.csproj

# 构建整个解决方案
dotnet build CADFontAutoReplace.slnx
```

版本对应关系：

| AutoCAD | 工程 | 输出 DLL |
| --- | --- | --- |
| 2018 | `src/AutoCAD/AFR-ACAD2018/` | `AFR-ACAD2018.dll` |
| 2019 | `src/AutoCAD/AFR-ACAD2019/` | `AFR-ACAD2019.dll` |
| 2020 | `src/AutoCAD/AFR-ACAD2020/` | `AFR-ACAD2020.dll` |
| 2021-2024 | `src/AutoCAD/AFR-ACAD2021/` 等 | `AFR-ACAD2021.dll` 等 |
| 2025-2027 | `src/AutoCAD/AFR-ACAD2025/` 等 | `AFR-ACAD2025.dll` 等 |

如果构建失败，先看目标 SDK、AutoCAD.NET 包还原、目标年份是否写错。不要因为一个版本壳失败就先改共享业务代码。

## 4. 第一次加载插件

有三种常见加载方式。

### VS 启动版本壳

适合日常 Debug。选择目标 `AFR-ACAD20XX` 工程启动，让 VS 拉起对应 AutoCAD。启动后执行 `AFR` / `AFRLOG` 验证命令面。

### 手动 NETLOAD

适合确认某个 DLL 是否真的可加载：

1. 构建目标版本壳。
2. 在 AutoCAD 命令行输入 `NETLOAD`。
3. 选择 `artifacts/bin/AFR-ACAD20XX/debug/AFR-ACAD20XX.dll`。
4. 首次 `NETLOAD` 会完成默认字体释放、配置初始化和自动加载注册，按命令行提示重启后再验证 Hook 行为。

### 部署器安装

适合验证发布/安装链路。部署器会扫描本机 AutoCAD、写注册表、复制插件 DLL、释放字体并处理 `FixedProfile.aws`。部署器相关改动必须检查 UAC、注册表、安装和卸载，不要只看 UI 能打开。

加载后先确认实际 DLL 路径。最常见的新手问题是源码改了，但 CAD 仍在运行旧部署目录里的 DLL。

## 5. 第一次看诊断

Debug 诊断文件是插件目录下的 `AFR_Diag_*.jsonl`。每行是一个 JSON 事件，不再使用旧文本日志。

先看这些字段：

- `seq`：还原真实时序。
- `status`：只使用 `START`、`OK`、`FAIL`、`SKIP`。
- `module` / `operation`：定位是启动、检测、替换、Hook 还是 UI。
- `context`：看文档、字体、Hook、计数等结构化数据。
- `error`：异常信息。

排查时按这个顺序：

1. 先按 `seq` 看插件是否完成初始化。
2. 过滤 `FAIL` / `SKIP`，找失败或跳过原因。
3. 看 `AutoCadFontHook.Install` 是否尝试安装 `LdFileHook` / `ShpLoadHook`。
4. 看 `ExecutionController` 是否进入当前文档处理流程。
5. 最后看 `LdFileHook` / `ShpLoadHook` 是否有真实 hit 和 redirect。

Hook 诊断必须分清三层：安装成功、收到 native 请求、实际 redirect。只有实际 redirect 才能算运行时映射成功。

## 6. 你要改什么，就从哪里开始

| 任务 | 先看哪里 | 第一验证动作 |
| --- | --- | --- |
| 新增普通 CAD 命令 | `CommandNames.cs`、`AFR.AutoCAD/Commands`、版本壳 `PluginEntry.cs` | CAD 命令行能执行，Release/Debug 暴露范围正确 |
| 新增 Debug 诊断命令 | Debug 命令文件、`#if DEBUG` 注册 | Debug 可执行，Release 中不可见 |
| 改字体检测 | `FontDetector`、`ShxFontAvailabilityIndex`、`TrueTypeFontAvailabilityIndex` | UI、检测、Hook 对同一字体可用性判断一致 |
| 改样式表替换 | `FontReplacer`、`ExecutionController`、`AFRLOG` | SHX 主字体、大字体、TrueType、样式表 `@TrueType` 都覆盖 |
| 改 Hook | `LdFileHook`、`ShpLoadHook`、`NativeFontHookProfile` | 真实 `HookHandler` hit/redirect，2027 ABI 和 fail-closed 行为正确 |
| 改 AFRLOG | `AfrCommands`、`DocumentContextManager`、UI ViewModel | 原始检测、仍缺失、运行时映射三类数据来源不混淆 |
| 改部署器 | `src/AFR.Deployer`、`AFR.HostIntegration` | UAC、注册表、嵌入资源、安装/卸载、字体释放都验证 |
| 改发布流程 | `Publish-ReleaseAssets.ps1`、Release workflow、`Version.props` | `artifacts/ReleaseAssets` 三件套生成且名称带版本 |
| 改文档 | README、本文、交接文档、仓库记忆 | 搜索旧入口和历史链路，确认没有误导性残留 |

## 7. 第一次做小改动

建议新手先做文档或 Debug-only 诊断类改动，不要从 native Hook 热路径开始。

### 示例 A：修改 README 开发入口

应改：

- `README.md`
- 如果事实变化影响维护者，也同步 `docs/developer-guide.md`
- 如果是长期行为规则，也同步 `.github/copilot-instructions.md`

验证：

```powershell
git diff --check
rg -n "旧入口名或旧关键词" README.md docs .github
```

不要改：

- `src/AutoCAD/AFR.AutoCAD/FontMapping/`，除非文档改动来自实际 Hook 行为变化。

### 示例 B：新增 Debug-only 诊断命令

应改：

- `src/AFR.Core/Constants/CommandNames.cs`
- `src/AutoCAD/AFR.AutoCAD/DebugCommands/YourDebugCommand.cs`
- 需要注册时确认 `CommandClass` 也受 Debug 条件控制
- README 或本文中的命令说明

验证：

- Debug 构建下 CAD 能执行命令。
- Release 构建下命令不可见、不可执行。
- 不要通过 `CommandMethod` 注册隐藏维护入口类命令。

## 8. 出问题先看这里

### CAD 提示“无此命令”

通常是命令类没有被 `CommandClass` 注册，或 Debug-only 命令没有进入当前构建。先检查 `CommandNames.cs`、`CommandMethod`、版本壳 `PluginEntry.cs`。

### 改了代码但 CAD 行为没变化

先确认实际加载 DLL 路径。常见原因是 CAD 仍在运行部署目录旧 DLL、未重启 AutoCAD、首次 `NETLOAD` 后还没重启、或 Debug/Release 分支不同。

### AFRLOG 没有内联映射记录

先看 `DocumentContextManager` 是否保存了运行时映射结果，再看 `FontRuntimeMappingStore` 是否有真实 Hook redirect。不要在 UI 层根据字体名自行推导成功项。

### Hook 安装了但没有 redirect

安装只说明 patch 尝试成功，不说明目标 DWG 触发了 native 请求。继续查 `LdFileHook.HookHandler` / `ShpLoadHook.HookHandler` hit、bypass 原因和 redirect 计数。

### Release 看不到 Debug 命令

这是正常结果。`AFRVIEW` 这类诊断命令只应在 Debug 暴露。若 Release 能看到 Debug 命令，说明条件编译边界错了。

### 改 Hook 后 CAD 崩溃或卡死

先回到最小改动，检查 ABI、入口 prefix、prologue、递归保护和日志限流。不要同时扩大 Hook 覆盖面和修改替换策略。

## 9. 项目结构和分层

```text
src/AFR.Core              命令常量、配置、日志、模型和无 AutoCAD SDK 的共享服务
src/AFR.UI                插件侧 WPF 窗口与 ViewModel，不引用 AutoCAD SDK
src/AFR.HostIntegration   部署器与插件共用的字体释放、AWS 弹窗抑制能力
src/AFR.Polyfills         旧 .NET Framework 版本壳兼容补丁
src/AutoCAD/AFR.AutoCAD   AutoCAD 命令、字体检测替换、Hook 和执行编排
src/AutoCAD/AFR-ACAD20XX  版本壳：目标框架、平台常量、PluginEntry、CommandClass
src/AFR.Deployer          独立 WPF 部署器
tools                     发布脚本
docs                      使用与开发文档
```

分层硬约束：

- `AFR.Core`、`AFR.UI`、`AFR.HostIntegration`、`AFR.Polyfills` 禁止引用 AutoCAD SDK。
- `AFR.AutoCAD` 才能持有 AutoCAD 托管 API 类型、事务、编辑器、命令、Hook 和执行流程。
- `AFR.Deployer` 不引用 AutoCAD SDK，也不直接引用插件项目；它从标准 Release 输出嵌入插件 DLL 与 `.cad.json`。
- 版本壳只做版本适配，不承载业务逻辑。

## 10. 当前执行流程

`ExecutionController.Execute` 是当前图纸字体处理主流程，修改前必须先读它。

当前顺序：

1. 检查文档、配置、首次加载和重复执行状态。
2. 获取文档写锁，建立 `FontDetectionContext` 和数据库级 Hook scope。
3. 使用 `RuntimeMappingStateScope.Begin(dbScope)` 清理本次文档运行时映射结果和文件级 Hook 状态。
4. `FontDetector.DetectMissingFonts()` 只读检测样式表原始缺失字体。
5. `DocumentContextManager.StoreDetectionResults()` 保存原始检测结果，供 `AFRLOG` 展示主列表。
6. `FontReplacer.ReplaceMissingFonts()` 对样式表缺失字体执行最终写回。
7. 二次 `DetectMissingFonts()`，通过 `StoreStillMissingResults()` 保存仍缺失样式。
8. 如果发生样式表写回，`MarkAffectedTextGraphicsModified()` 标记受影响文字、属性和块引用。
9. `Editor.Regen()` 触发 AutoCAD 原生展开和绘制流程，让内联字体进入文件级 Hook。
10. `FontRuntimeMappingStore.GetRuntimeMappingResults()` 只采集真实 `HookHandler` redirect 结果。
11. `StoreRuntimeFontMappingResults()` 保存运行时映射结果，供 `AFRLOG` 使用。
12. 写入 Hook 计数、日志统计和 `DocumentContextManager.MarkExecuted(doc)`。

不要把样式表永久写回、Hook 运行时映射和 `AFRLOG` 展示混成一个步骤。样式表写回改变 DWG 数据；Hook 映射只证明当前加载请求被重定向；`AFRLOG` 只是读取已保存的上下文和实时检测结果。

## 11. 字体处理规则

样式表规则：

- `ShapeFile` 样式用于复杂线型，检测和替换都必须跳过。
- SHX 主字体缺失写回配置 `MainFont`。
- SHX 大字体缺失写回配置 `BigFont`。
- 普通 TrueType 缺失写回配置 `TrueTypeFont`。
- 样式表 `@TrueType` 先去掉 `@` 检查基础 TrueType：基础字体存在则跳过，基础字体不存在才写回配置刷新时预解析的 `@TrueType` 专用字体。
- 替换 TrueType 时必须清空 `BigFontFileName`、`FileName`，再写入 `FontDescriptor`。
- 替换 SHX 时必须清空残留 `FontDescriptor`，避免样式表处在混合状态。

内联运行时规则：

- MText 内联字体不改写 `MText.Contents`。
- 当前默认链路不安装来源级 MText Hook，不运行 MText 候选扫描作为修复关键路径。
- 内联 SHX / `@SHX` 只有实际进入 `LdFileHook` 并 redirect 后，才能计入运行时映射。
- 内联 TrueType / `@TrueType` 只有实际进入 `ShpLoadHook` 并 redirect 后，才能计入运行时映射。
- `@SHX` 先尝试去 `@` 后基础 SHX；基础不可用时才映射配置 SHX。
- `@TrueType` 先检查基础 TrueType；基础存在则保留原请求，基础缺失才映射到预解析的 `@TrueType` 专用字体。

字体可用性必须统一走共享索引：

- `ShxFontAvailabilityIndex` 负责 SHX 可用性、主/大字体分类、主/大/全量字体快照。
- `TrueTypeFontAvailabilityIndex` 负责 DirectWrite 系统字体族、CAD TrueType 文件兜底，以及配置字体的 `@TrueType` 预解析。
- UI 字体列表、检测逻辑和 Hook 判断不能各自维护一套不同的字体列表。

## 12. Hook 维护边界

当前默认只安装 `LdFileHook` 和 `ShpLoadHook`。`AutoCadFontHook.Install()` 负责初始化共享字体索引并安装这两个插件级持久 Hook；`ExecutionController` 只清理文档级运行时状态，不按文档安装或卸载 Hook。

必须保留的基础设施：

- `NativeInlineHook`
- `NativeHookTarget`
- `NativeFontHookProfile`
- `INativeFontHookExportsProvider`

`LdFileHook` 边界：

- 只负责进入 AutoCAD `ldfile` 的 SHX 文件级请求。
- 处理 `param2=0/4` 的主字体和大字体请求。
- `param2=2` shape file 必须跳过。
- 不处理 TrueType face，不把 TrueType 放进 SHX 兜底。
- 成功证据是 `LdFileHook.HookHandler` 真实命中、redirect 计数和 `FontRuntimeMappingStore` 记录。

`ShpLoadHook` 边界：

- 是严格 TrueType / `@TrueType` 文件级映射执行点。
- 采样 `fileName`、`arg5`、`arg6`，但只有 `IsConfirmedTrueTypeRequest(...)` 认可的请求才允许替换。
- `.shx`、已知 SHX、可归一化为已知 SHX 的无扩展名请求必须放行。
- `fileName` / `arg5` 上 `param2=0/4` 的无扩展名请求默认属于 SHX 主字体/大字体加载槽位，不能兜底成 TrueType。
- ambiguous 请求只能 pass-through，可记录受限的 `TrueTypeStrictBypass` 诊断，不能为了“多修一点”扩大替换范围。

版本和安全边界：

- 2018-2026 的 `shpload` 使用 `_N00HH` 的 `int/int` ABI。
- 2027 的 `shpload` 使用 `_N0022` 的 `bool/bool` ABI，不得复用 legacy delegate。
- 导出名缺失、入口 prefix 不匹配或 prologue 扫描失败必须 fail-closed 跳过安装。
- RVA 不匹配只作为 build 指纹漂移提示，不能替代 prefix / prologue 安装硬闸。
- Hook 热路径内避免分配、避免阻塞、避免未受限日志、避免递归破坏 `_inHook` 保护。

不要恢复来源级样式 Hook、MText 来源 Hook、MText 候选扫描、`MText.Contents` 改写或 setter 直接替换作为默认修复路径。若未来必须重新引入，先写清证据边界、失败模式和 CAD 实测验收，再改代码。

## 13. UI 与日志

`AFR`、`AFRLOG` 和 `AFRVIEW` 的数据来源不同，不能互相替代。

- `AFR` 修改全局配置，配置变化后应刷新共享字体索引；若文档已有原始检测结果，应按新配置重新覆盖样式。
- `AFRLOG` 每次打开都会重新检测当前文档，同时读取 `DocumentContextManager` 中的原始检测、仍缺失结果和 Hook 运行时映射。
- `AFRLOG` 手动替换只调用 `FontReplacer.ReplaceByStyleMapping()` 写当前图纸样式表，不修改注册表全局配置。
- `AFRLOG` 中的内联映射数量只能来自 `FontRuntimeMappingStore`，不能由 UI 层根据字体名重新推导。
- `AFRVIEW` 只用于 Debug 下查看 MText / MLeader 格式与样式诊断，不是修复命令。

新增诊断日志时使用结构化方法：`Start`、`Ok`、`Fail`、`Skip`、`RunStep` 或已有领域方法。`status` 保持 `START`、`OK`、`FAIL`、`SKIP`，不要恢复旧文本日志格式。

## 14. 部署与发布

部署器事实：

- `AFR.Deployer` 是 `net10.0-windows`、`win-x64`、自包含单文件 WPF 应用。
- `app.manifest` 请求 `requireAdministrator`，安装/卸载时应预期 UAC。
- 部署器通过 `AFR.HostIntegration` 共用内嵌 SHX 字体释放和 `FixedProfile.aws` 弹窗抑制能力。
- 插件 DLL 与 `.cad.json` 从 `artifacts/bin/AFR-ACAD*/release/` 嵌入部署器资源。
- 不需要 Windows App Runtime 作为外置依赖；不要重新引入该要求，除非代码确实改为依赖 WinAppSDK。

发布资产由 `tools/Publish-ReleaseAssets.ps1` 统一生成：

```powershell
./tools/Publish-ReleaseAssets.ps1
./tools/Publish-ReleaseAssets.ps1 -SkipPluginBuild
```

输出约定：

```text
publish/AFR.Deployer/AFR-Deployer.exe
artifacts/ReleaseAssets/AFR-Deployer_vX.Y.Z.exe
artifacts/ReleaseAssets/AFR-DLL_vX.Y.Z.zip
artifacts/ReleaseAssets/Fonts.zip
```

发版版本号来自根目录 `Version.props`。发布脚本不接受模型、模型清单、训练包或原生推理运行时参数。

## 15. 提交前检查

常规变更：

- `dotnet build CADFontAutoReplace.slnx -c Debug`
- `dotnet build CADFontAutoReplace.slnx -c Release`
- 文档变更至少运行 `git diff --check`

命令变更：

- 检查 `CommandNames.cs`、`CommandMethod`、`CommandClass`。
- Debug-only 命令检查 Release 中不可见。
- `AFRUNLOAD` 仍保持隐藏路由，不进入命令补全。

Hook 变更：

- 检查启动日志是否只安装预期 Hook。
- 检查真实 `HookHandler` hit / redirect，不把安装成功当成功映射。
- 检查 `LdFileHook` SHX-only、`ShpLoadHook` confirmed TrueType-only 边界。
- 检查 2027 `_N0022 bool/bool` ABI，不得错用 2018-2026 分支。
- CAD 实测要包含问题 DWG、日志证据和显示结果。

部署器变更：

- 检查 UAC、注册表扫描、安装、卸载。
- 检查插件 DLL 和 `.cad.json` 嵌入资源。
- 检查 `FixedProfile.aws` 和字体释放路径。
- 发布相关变更验证 `tools/Publish-ReleaseAssets.ps1`。

文档变更：

- README 面向用户，避免写内部实现细节过多。
- 本文必须能指导新手完成首次开发闭环，同时保留维护边界。
- `PROJECT_HANDOVER.md` 是短版交接，不要塞成长篇教程。
- `.github/copilot-instructions.md` 是仓库长期协作记忆，行为变化时必须同步。

## 16. 禁止事项

- 禁止把 AutoCAD SDK 类型放入 `AFR.Core`、`AFR.UI`、`AFR.HostIntegration` 或 `AFR.Polyfills`。
- 禁止恢复 DBText、GlyphCore、WenShu、AI、训练、候选包、模型、报告、native decode evidence 或补绘链路，除非先重新定义完整边界。
- 禁止恢复来源级 Hook、MText 候选扫描、`MText.Contents` 改写或 setter 直接替换作为默认修复路径。
- 禁止让 `ShpLoadHook` 把未知无扩展名、SHX-like 请求或 `fileName/arg5 + param2=0/4` 兜底成 TrueType。
- 禁止用 Hook 安装成功、早期登记、候选扫描或 UI 推导替代真实 `HookHandler` redirect 证据。
- 禁止手工复制插件 DLL 到部署器资源目录；新增版本应先让发布脚本生成标准 Release 输出。
- 禁止把本地日志、截图、浏览器 profile、反汇编结果、训练数据或构建产物提交到仓库。

## 17. 相关文档

- [字体 Hook 证据与边界](font-hook-evidence-and-boundaries.md)
- [Git 分支管理摘要](git-branch-guidelines.md)
- [AutoCAD 原版 SHX 字体清单](autodesk-fonts.md)
