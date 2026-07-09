# 开发者指南

本文面向普通贡献者，帮助第一次接触本项目的开发者完成一条完整开发路径：

1. 准备本机开发环境。
2. 克隆仓库并跑通构建。
3. 根据改动类型选择开发入口。
4. 在 AutoCAD 或部署器中验证结果。
5. 提交 PR，并补充必要的验证信息。

本文只覆盖开发和验证流程，不覆盖发布流程。用户安装和命令说明请看 [README](../README.md)；分支命名和 PR 规则请看 [Git 分支与 PR 规则](git-branch-guidelines.md)。

## 开发路线

先判断你要做哪类改动，再按对应路线走。

| 你要做什么 | 是否需要 AutoCAD | 主要验证 |
| --- | --- | --- |
| 修改 README、文档、说明文字 | 不需要 | `git diff --check`，检查链接和术语一致。 |
| 修改构建脚本、共享工具、非运行时代码 | 通常不需要 | 构建受影响项目。 |
| 修改插件命令、字体检测、替换逻辑、Hook | 需要 | 构建对应版本插件，在同版本 AutoCAD 中 `NETLOAD` 验证。 |
| 修改部署器界面或安装逻辑 | UI 改动可不需要；真实安装/卸载需要 | 构建部署器；安装/卸载流程需要管理员权限和测试 AutoCAD。 |

如果你没有 AutoCAD，也可以贡献文档、脚本、UI 文案或不依赖 AutoCAD 的逻辑。提交 PR 时说明“未做 AutoCAD 运行时验证”即可。

## 准备环境

### 必需工具

- Windows 10/11。
- Git。
- Visual Studio 2022 或更新版本，并安装“.NET 桌面开发”工作负载。
- 根目录 [global.json](../global.json) 指定的 .NET SDK。当前指定版本是 `10.0.201`，允许 `latestFeature` 滚动。

### 可选工具

- 目标版本 AutoCAD：修改插件运行时、字体检测、Hook 或 CAD 命令时需要。
- 管理员权限：验证部署器安装、卸载、写入 AutoCAD 配置和释放字体时需要。

### 检查环境

打开 PowerShell，运行：

```powershell
git --version
dotnet --version
dotnet --info
```

如果 `dotnet --version` 不满足 [global.json](../global.json)，先安装对应 SDK，再重新打开 PowerShell。

## 获取代码

没有主仓库写权限时，先在 GitHub 上 Fork，然后克隆自己的 Fork。

```powershell
git clone https://github.com/<your-name>/CADFontAutoReplace.git
cd CADFontAutoReplace
git remote add upstream https://github.com/splrad/CADFontAutoReplace.git
git fetch upstream
```

如果你已经有主仓库写权限，也可以直接克隆主仓库：

```powershell
git clone https://github.com/splrad/CADFontAutoReplace.git
cd CADFontAutoReplace
```

开始改动前，先确认工作区状态：

```powershell
git status
```

建议每个任务新建一个分支，分支命名规则见 [Git 分支与 PR 规则](git-branch-guidelines.md)。

如果你克隆的是 Fork：

```powershell
git checkout -b docs/developer-guide upstream/main
```

如果你直接克隆主仓库：

```powershell
git checkout -b docs/developer-guide origin/main
```

## 第一步：跑通构建

项目为不同 AutoCAD 年份提供不同版本壳。命令里的 `20XX` 要替换成你的目标版本，例如 `2026`。

```powershell
# 构建一个 AutoCAD 版本插件
dotnet build src/AutoCAD/AFR-ACAD20XX/AFR-ACAD20XX.csproj

# 构建部署器
dotnet build src/AFR.Deployer/AFR.Deployer.csproj

# 构建整个解决方案
dotnet build CADFontAutoReplace.slnx
```

如果你只改文档，不需要构建整个解决方案，运行下面命令即可：

```powershell
git diff --check
```

### AutoCAD 版本工程

| AutoCAD | 工程 |
| --- | --- |
| 2018 | `src/AutoCAD/AFR-ACAD2018/AFR-ACAD2018.csproj` |
| 2019 | `src/AutoCAD/AFR-ACAD2019/AFR-ACAD2019.csproj` |
| 2020 | `src/AutoCAD/AFR-ACAD2020/AFR-ACAD2020.csproj` |
| 2021 | `src/AutoCAD/AFR-ACAD2021/AFR-ACAD2021.csproj` |
| 2022 | `src/AutoCAD/AFR-ACAD2022/AFR-ACAD2022.csproj` |
| 2023 | `src/AutoCAD/AFR-ACAD2023/AFR-ACAD2023.csproj` |
| 2024 | `src/AutoCAD/AFR-ACAD2024/AFR-ACAD2024.csproj` |
| 2025 | `src/AutoCAD/AFR-ACAD2025/AFR-ACAD2025.csproj` |
| 2026 | `src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj` |
| 2027 | `src/AutoCAD/AFR-ACAD2027/AFR-ACAD2027.csproj` |

### 构建产物在哪里

所有构建产物固定输出到根目录 `artifacts` 下。

插件 Debug DLL：

```powershell
artifacts\bin\AFR-ACAD20XX\debug\AFR-ACAD20XX.dll
```

插件 Release DLL：

```powershell
artifacts\bin\AFR-ACAD20XX\release\AFR-ACAD20XX.dll
```

部署器 Debug EXE：

```powershell
artifacts\bin\AFR.Deployer\debug_win-x64\AFR-Deployer.exe
```

部署器 Release EXE：

```powershell
artifacts\bin\AFR.Deployer\release_win-x64\AFR-Deployer.exe
```

例如 AutoCAD 2026 Debug 插件 DLL 位于：

```powershell
artifacts\bin\AFR-ACAD2026\debug\AFR-ACAD2026.dll
```

## 第二步：选择开发入口

### 路线 A：只改文档

适合 README、开发文档、分支规则、字体清单等纯文档改动。

1. 修改对应 Markdown 文件。
2. 检查链接、命令和路径是否仍然准确。
3. 运行：

```powershell
git diff --check
```

4. 查看改动：

```powershell
git diff
```

文档 PR 不需要编造构建结果；没有运行构建就直接说明“文档改动，未运行构建”。

### 路线 B：修改插件并用 NETLOAD 验证

适合插件命令、字体检测、替换逻辑、日志和 Hook 相关改动。

1. 构建与你本机 AutoCAD 匹配的版本，例如 AutoCAD 2026：

```powershell
dotnet build src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj
```

2. 启动同版本 AutoCAD。
3. 在 AutoCAD 命令行输入 `NETLOAD`。
4. 选择刚构建出的 DLL：

```powershell
artifacts\bin\AFR-ACAD2026\debug\AFR-ACAD2026.dll
```

5. 首次 `NETLOAD` 会初始化配置、释放默认字体并注册自动加载；按命令行提示重启 AutoCAD。
6. 重启后执行 `AFR`，确认字体配置窗口可以打开。
7. 打开测试 DWG，执行 `AFRLOG`，确认能看到字体检测和替换相关信息。

单 DLL / `NETLOAD` 路径不包含部署器的 `FixedProfile.aws` 弹窗抑制处理。需要验证安装、卸载和缺失 SHX 弹窗抑制时，使用部署器路线。

### 路线 C：用 Visual Studio 调试插件

适合需要断点调试的插件改动。

1. 打开根目录 [CADFontAutoReplace.slnx](../CADFontAutoReplace.slnx)。
2. 找到与你 AutoCAD 版本一致的 `AFR-ACAD20XX` 工程。
3. 将该工程设为启动项目。
4. 检查 `Properties/launchSettings.json` 中的 `executablePath` 是否指向本机 `acad.exe`。
5. 按 F5 启动调试。
6. AutoCAD 启动后执行 `AFR` / `AFRLOG` 验证命令。

如果你的 AutoCAD 安装路径不同，只改本机调试配置。提交前运行 `git diff`，确认没有把个人安装路径带进 PR。

### 路线 D：开发部署器

部署器是独立 WPF 程序，负责一键安装、卸载和状态展示。它不引用 AutoCAD SDK，也不直接引用某个插件项目；真实安装时会把内嵌的插件 DLL 提取到用户选择的部署目录，并写入 AutoCAD 自动加载注册表项。

部署器主要做这些事：

- 扫描本机 AutoCAD 2018-2027 的注册表配置。
- 展示每个版本的状态：未安装、已安装、旧版、DLL 缺失。
- 把插件 DLL 写入部署目录。
- 写入或清理 AutoCAD 自动加载注册表项。
- 释放内嵌默认 SHX 字体到对应 AutoCAD 的 `Fonts` 目录。
- 安装或卸载时处理本插件写入的 `FixedProfile.aws` 缺失 SHX 弹窗抑制节点。
- 检测 AutoCAD 是否正在运行，并在运行中禁用安装/卸载操作。

部署器不负责修改 DWG 内容，也不验证字体替换算法本身；字体替换行为仍在插件侧用 AutoCAD 和测试图纸验证。

#### 部署器相关目录

| 位置 | 作用 |
| --- | --- |
| `src/AFR.Deployer/Views` | WPF 窗口和界面布局。 |
| `src/AFR.Deployer/ViewModels` | 主窗口状态、命令、扫描结果和按钮可用性。 |
| `src/AFR.Deployer/Services/CadRegistryScanner.cs` | 扫描 AutoCAD 注册表和插件部署状态。 |
| `src/AFR.Deployer/Services/PluginDeployer.cs` | 提取插件 DLL，写入自动加载注册表项。 |
| `src/AFR.Deployer/Services/PluginUninstaller.cs` | 删除插件自动加载项和可清理文件。 |
| `src/AFR.Deployer/Services/EmbeddedFontPatcher.cs` | 释放内嵌 SHX 字体到 CAD `Fonts` 目录。 |
| `src/AFR.Deployer/Services/AwsHideableDialogPatcher.cs` | 调用共享逻辑处理 `FixedProfile.aws`。 |
| `src/AFR.HostIntegration` | 部署器和插件共用的字体释放、AWS 弹窗抑制能力。 |

#### 构建部署器

只检查 UI 或普通编译时，直接构建部署器：

```powershell
dotnet build src/AFR.Deployer/AFR.Deployer.csproj
```

如果要验证真实安装流程，先构建要测试的插件 Release 输出，因为部署器从固定目录嵌入 `AFR-ACAD*.dll` 和 `.cad.json`：

```powershell
dotnet build src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj -c Release
dotnet build src/AFR.Deployer/AFR.Deployer.csproj
```

部署器会读取的插件输入示例：

```powershell
artifacts\bin\AFR-ACAD2026\release\AFR-ACAD2026.dll
artifacts\bin\AFR-ACAD2026\release\AFR-ACAD2026.cad.json
```

如果要在部署器列表中验证多个 AutoCAD 版本，先构建这些版本各自的 Release 输出。新增 AutoCAD 年份时，必须确认版本壳 Release 构建会生成同名 `.cad.json`；否则部署器无法把该版本作为可安装项嵌入。

#### 运行部署器

部署器 manifest 要求管理员权限。调试界面时可以从 Visual Studio 启动；验证安装/卸载时，建议直接运行开发构建产物：

```powershell
artifacts\bin\AFR.Deployer\debug_win-x64\AFR-Deployer.exe
```

运行前关闭所有 AutoCAD 进程。部署器检测到 AutoCAD 正在运行时，会禁用安装/卸载按钮。

#### 安装验证

至少检查：

1. 已安装的 AutoCAD 版本显示为可操作，未安装版本不可操作。
2. 选择部署目录后点击安装，目标目录出现对应 `AFR-ACAD20XX.dll`。
3. 对应 AutoCAD 配置下写入 `Applications\<AppName>` 自动加载项。
4. 部署器状态从“待安装”变为“已安装”或“最新版”。
5. 默认 SHX 字体被释放到该 CAD 的 `Fonts` 目录；失败时应只显示警告，不阻断安装主流程。
6. 缺失 SHX 弹窗抑制只处理本插件负责的 `FixedProfile.aws` 节点，不改动其它节点。
7. 启动 AutoCAD 后执行 `AFR` / `AFRLOG`，确认插件能正常加载。

#### 卸载验证

至少检查：

1. 已安装版本可以勾选并执行卸载。
2. 目标 DLL 被删除；如果文件被占用，部署器应显示警告并继续清理注册表。
3. `Applications\<AppName>` 自动加载项被删除，不删除 `Applications` 父项或其它插件项。
4. 本插件写入的 `FixedProfile.aws` 抑制节点被清理。
5. 用户安装后手动修改过的外部注册表值不应被误删。
6. 卸载后重新扫描，状态回到“待安装”或对应未安装状态。

#### 常见部署器改动的验证重点

| 改动类型 | 重点验证 |
| --- | --- |
| UI 布局或文字 | 不同窗口宽度下内容不重叠，按钮可用性和状态文案正确。 |
| 扫描逻辑 | 未安装、已安装、旧版、DLL 缺失、多配置 profile 都能正确聚合。 |
| 安装逻辑 | DLL 提取、注册表写入、字体释放、AWS 抑制、部分失败警告。 |
| 卸载逻辑 | 文件删除失败不阻断注册表清理，只清理本插件拥有的项。 |
| 嵌入资源 | Release 插件 DLL 和 `.cad.json` 都进入部署器资源，新增版本能出现在列表中。 |
| 进程检测 | AutoCAD 运行时禁用安装/卸载，关闭后状态恢复。 |

没有 AutoCAD 环境时，可以完成 UI、构建和非运行时逻辑改动；PR 中补充未验证真实安装/卸载即可。

## 第三步：开始做一个改动

这一节给出常见改动的落点。先按小范围修改，跑通验证后再扩大范围。

### 修改文案或界面

| 场景 | 先看哪里 | 怎么验证 |
| --- | --- | --- |
| 插件窗口文字、按钮、布局 | `src/AFR.UI` | 构建目标插件，`NETLOAD` 后打开 `AFR`。 |
| 部署器文字、按钮、布局 | `src/AFR.Deployer/Views`、`src/AFR.Deployer/ViewModels` | 构建并运行部署器，检查不同状态下按钮和提示。 |
| README 或教程 | `README.md`、`docs/` | `git diff --check`，检查链接和命令。 |

UI 改动要确认窗口缩放后文字不重叠，按钮状态不会误导用户。

### 新增或修改 CAD 命令

先看：

- `src/AFR.Core/Constants/CommandNames.cs`
- `src/AutoCAD/AFR.AutoCAD/Commands`
- `src/AutoCAD/AFR.AutoCAD/DebugCommands`
- 对应版本壳的 `PluginEntry.cs`

验证：

1. 构建目标版本插件。
2. 在 AutoCAD 中 `NETLOAD`。
3. 输入命令名，确认命令可执行。
4. 如果是 Debug-only 命令，确认 Release 构建中不可见。

### 修改字体检测或替换

先看：

- `src/AutoCAD/AFR.AutoCAD/Services/FontDetector.cs`
- `src/AutoCAD/AFR.AutoCAD/Services/FontReplacer.cs`
- `src/AutoCAD/AFR.AutoCAD/FontMapping/ShxFontAvailabilityIndex.cs`
- `src/AutoCAD/AFR.AutoCAD/FontMapping/TrueTypeFontAvailabilityIndex.cs`
- `src/AutoCAD/AFR.AutoCAD/Hosting/ExecutionController.cs`
- `AFRLOG`

验证时至少覆盖：

- SHX 主字体。
- SHX 大字体。
- 普通 TrueType。
- `@TrueType`。
- `ShapeFile` 样式跳过逻辑。

字体可用性应统一走共享索引，不要在 UI、检测逻辑和 Hook 中各自维护独立字体列表。

### 修改运行时 Hook

先看：

- `src/AutoCAD/AFR.AutoCAD/FontMapping/LdFileHook.cs`
- `src/AutoCAD/AFR.AutoCAD/FontMapping/ShpLoadHook.cs`
- `src/AutoCAD/AFR.AutoCAD/FontMapping/NativeFontHookProfile.cs`
- `src/AutoCAD/AFR.AutoCAD/FontMapping/FontRuntimeMappingStore.cs`
- `AFRLOG`

验证重点：

1. Hook 安装成功。
2. 目标 DWG 触发真实 `HookHandler` hit。
3. redirect 记录写入 `FontRuntimeMappingStore`。
4. `AFRLOG` 能区分原始检测、仍缺失字体和运行时映射。

Hook 安装成功不等于运行时映射成功；必须看到真实 hit 和 redirect 记录。

### 修改部署器

先看“路线 D：开发部署器”。部署器改动通常要同时验证 UI 状态、扫描结果、安装、卸载和失败提示。

特别注意：

- 部署器不引用 AutoCAD SDK。
- 部署器不直接引用插件项目。
- 安装/卸载只能清理本插件拥有的注册表项、文件和 AWS 节点。
- AutoCAD 正在运行时不能执行安装/卸载。

## 项目地图

| 目录 | 作用 |
| --- | --- |
| `src/AFR.Core` | 命令常量、配置、日志、模型和无 AutoCAD SDK 的共享服务。 |
| `src/AFR.UI` | 插件侧 WPF 窗口与 ViewModel，不引用 AutoCAD SDK。 |
| `src/AFR.HostIntegration` | 部署器与插件共用的字体释放、AWS 弹窗抑制能力。 |
| `src/AFR.Polyfills` | 旧 .NET Framework 版本壳兼容补丁。 |
| `src/AutoCAD/AFR.AutoCAD` | AutoCAD 命令、字体检测替换、Hook 和执行编排。 |
| `src/AutoCAD/AFR-ACAD20XX` | 版本壳：目标框架、平台常量、`PluginEntry`、`CommandClass`。 |
| `src/AFR.Deployer` | 独立 WPF 部署器。 |
| `tools` | 辅助脚本。 |
| `docs` | 用户和贡献者文档。 |

### 分层规则

- `AFR.Core`、`AFR.UI`、`AFR.HostIntegration`、`AFR.Polyfills` 不引用 AutoCAD SDK。
- `AFR.AutoCAD` 承载 AutoCAD 托管 API、命令、事务、Hook 和图纸执行流程。
- `AFR.Deployer` 不引用 AutoCAD SDK，也不直接引用插件项目。
- `AFR-ACAD20XX` 版本壳只做版本适配，不放业务逻辑。

## 诊断日志

Debug 诊断文件在插件目录下，文件名类似 `AFR_Diag_*.jsonl`。每行是一个 JSON 事件。

优先看这些字段：

- `seq`：事件顺序。
- `status`：`START`、`OK`、`FAIL`、`SKIP`。
- `module` / `operation`：启动、检测、替换、Hook 或 UI。
- `context`：文档、字体、Hook、计数等结构化数据。
- `error`：异常信息。

建议排查顺序：

1. 按 `seq` 确认插件完成初始化。
2. 过滤 `FAIL` / `SKIP`，先处理明确失败原因。
3. 检查 `ExecutionController` 是否进入当前文档处理流程。
4. 检查 `AutoCadFontHook.Install` 是否尝试安装 `LdFileHook` / `ShpLoadHook`。
5. 检查 `LdFileHook` / `ShpLoadHook` 是否有真实 hit 和 redirect。

## 关键规则

### 字体处理

- `ShapeFile` 样式用于复杂线型，检测和替换都跳过。
- SHX 主字体缺失写回配置 `MainFont`。
- SHX 大字体缺失写回配置 `BigFont`。
- 普通 TrueType 缺失写回配置 `TrueTypeFont`。
- 样式表 `@TrueType` 先检查去掉 `@` 后的基础 TrueType；基础字体存在则保留，基础字体缺失才写回预解析的 `@TrueType` 专用字体。
- 替换 TrueType 时清空 `BigFontFileName` 和 `FileName`，再写入 `FontDescriptor`。
- 替换 SHX 时清空残留 `FontDescriptor`，避免样式表处于混合状态。

### 内联字体运行时映射

- MText 内联字体不改写 `MText.Contents`。
- 内联 SHX / `@SHX` 只有进入 `LdFileHook` 并 redirect 后，才计入运行时映射。
- 内联 TrueType / `@TrueType` 只有进入 `ShpLoadHook` 并 redirect 后，才计入运行时映射。
- `@SHX` 先尝试基础 SHX，基础不可用时才映射到配置 SHX。
- `@TrueType` 先检查基础 TrueType，基础缺失才映射到预解析的 `@TrueType` 专用字体。

### Hook 边界

- 默认只安装 `LdFileHook` 和 `ShpLoadHook`。
- `LdFileHook` 只处理 AutoCAD `ldfile` 的 SHX 文件级请求。
- `ShpLoadHook` 只处理确认过的 TrueType / `@TrueType` 文件级请求。
- 导出名缺失、入口 prefix 不匹配或 prologue 扫描失败时 fail closed，跳过安装。
- Hook 热路径避免分配、阻塞、未受限日志和破坏递归保护。

## 常见问题

### `dotnet` 命令不存在

安装 [global.json](../global.json) 指定的 .NET SDK，并重新打开 PowerShell。

```powershell
dotnet --version
```

### 构建提示找不到目标框架或 SDK

先确认安装了对应 .NET SDK 和 Visual Studio “.NET 桌面开发”工作负载。旧版本 AutoCAD 壳使用 .NET Framework 目标框架，新版本壳使用 `net8.0-windows` 或 `net10.0-windows`。

### 构建成功后找不到 DLL

不要在项目目录里手动翻找，直接到固定输出目录查看。把 `2026` 换成你的目标版本：

```powershell
Get-ChildItem artifacts\bin\AFR-ACAD2026 -Recurse -Filter AFR-ACAD2026.dll
```

部署器输出：

```powershell
Get-ChildItem artifacts\bin\AFR.Deployer -Recurse -Filter AFR-Deployer.exe
```

### AutoCAD 提示“无此命令”

常见原因：

- 加载的 DLL 不是刚构建出来的 DLL。
- 命令类没有在版本壳 `PluginEntry.cs` 中注册。
- Debug-only 命令在 Release 构建中不可见。

先确认 AutoCAD 实际加载路径，再检查命令注册。

### 改了代码但行为没变化

通常是 AutoCAD 仍加载旧 DLL，或首次 `NETLOAD` 后没有重启。重新构建、重新 `NETLOAD`，必要时执行 `AFRUNLOAD` 后重启 AutoCAD。

### `AFRLOG` 没有内联映射记录

先确认当前 DWG 是否触发了真实字体加载请求，再查 `FontRuntimeMappingStore` 是否有 Hook redirect 记录。不要根据字体名推导映射成功。

### Hook 安装了但没有 redirect

安装成功不等于目标图纸触发了请求。继续检查 `LdFileHook.HookHandler` / `ShpLoadHook.HookHandler` hit、bypass 原因和 redirect 计数。

### 部署器安装/卸载按钮不可用

先确认是否有 AutoCAD 进程正在运行。部署器检测到 AutoCAD 运行时会禁用安装/卸载。

### 部署器列表里没有目标版本

先确认本机安装了对应 AutoCAD，再确认目标版本插件 Release 输出和 `.cad.json` 已生成。示例：

```powershell
dotnet build src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj -c Release
dotnet build src/AFR.Deployer/AFR.Deployer.csproj
```

## 提交 PR 前

按改动范围选择检查项：

| 改动范围 | 至少运行 |
| --- | --- |
| 文档 | `git diff --check` |
| 单个插件版本 | `dotnet build src/AutoCAD/AFR-ACAD20XX/AFR-ACAD20XX.csproj` |
| 部署器 | `dotnet build src/AFR.Deployer/AFR.Deployer.csproj` |
| 跨项目共享代码 | `dotnet build CADFontAutoReplace.slnx` |

额外检查：

- 命令改动：检查 `CommandNames.cs`、`CommandMethod`、`CommandClass`，并在 AutoCAD 命令行验证。
- Debug-only 命令：确认 Release 中不可见。
- Hook 改动：确认真实 `HookHandler` hit/redirect，不能只看安装成功。
- 部署器改动：检查安装、卸载、字体释放、AWS 回滚和状态刷新。
- 建议提交带 `Signed-off-by`，可用 `git commit -s` 创建；当前 DCO 检查只提示，不作为合并门禁。

PR 标题和说明通常会自动生成。提交前检查生成内容是否准确，必要时补充：

- 运行过的命令。
- 用哪个 AutoCAD 版本验证。
- 没有覆盖的环境或版本。
- 文档改动是否只做了格式和说明调整。

## 相关文档

- [Git 分支与 PR 规则](git-branch-guidelines.md)
- [AutoCAD 原版 SHX 字体清单](autodesk-fonts.md)
