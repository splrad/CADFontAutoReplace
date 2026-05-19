# CADFontAutoReplace 项目技术交接与长期维护文档

> 生成日期：2026-05-19  
> 适用仓库：`E:\Software Plugin Project\CADFontAutoReplace`  
> 当前版本来源：`Version.props`，`PluginDisplayVersion=9.1.0`，`PluginBuildId=20260504.1`  
> 文档用途：给未来维护者、未来的项目作者，以及后续接手本仓库的 AI 使用。它不是普通 README，而是长期维护恢复上下文用的工程交接文档。

## 0. 阅读说明与可信度边界

本文档基于当前代码仓库中的真实文件、类名、命令名、构建脚本、发布脚本和配置文件整理。凡是无法从当前源码或仓库文件直接确认的信息，均标注为“待确认”。

长期维护时请先阅读本文档，再阅读以下几个文件：

- `README.md`：面向用户和普通开发者的项目说明。
- `.github/copilot-instructions.md`：仓库维护记忆和 AI 修改规则。当前工作区中该文件显示为未跟踪文件，是否纳入 Git 需要确认。
- `CADFontAutoReplace.slnx`：解决方案结构。
- `src/AutoCAD/AFR.AutoCAD/PluginEntryBase.cs`：AutoCAD 插件生命周期入口。
- `src/AutoCAD/AFR.AutoCAD/Commands/AfrCommands.cs`：正式命令 `AFR` 与 `AFRLOG`。
- `src/AutoCAD/AFR.AutoCAD/Services/ExecutionController.cs`：主自动执行流程。
- `src/AFR.Core/GlyphCore/TextRepair/`：GlyphCore DBText AI 修复核心逻辑。
- `tools/Publish-ReleaseAssets.ps1`：本地发布资产生成脚本。
- `.github/workflows/release-build.yml`：GitHub Release 自动构建流程。

维护者和 AI 不应只看某一个文件就修改核心行为。本项目有 AutoCAD 版本差异、原生 Hook、注册表、嵌入资源、部署器、训练工具、模型 schema 多条链路。单点修改很容易造成 CAD 中可加载但运行时失效。

## 1. 项目概述

CADFontAutoReplace 是一个面向 AutoCAD 的字体自动替换与文字修复插件。项目当前以 `AFR` 作为插件命令前缀，支持 AutoCAD 2018 到 AutoCAD 2027 的多版本插件 DLL 构建，并提供一个独立的 WPF 部署器 `AFR-Deployer.exe`。

核心能力包括：

- 检测 DWG 中缺失的 SHX 字体、BigFont 字体和 TrueType 字体。
- 根据用户配置自动替换缺失字体。
- 在 AutoCAD 字体加载阶段通过 `LdFileHook` 重定向缺失字体，避免 CAD 原生缺字弹窗和乱码显示问题。
- 扫描并修复 MText 内联字体片段，例如 `\F...|` 和 `\f...|`。
- 使用本地闭环的 GlyphCore AI 模型修复 DBText 文字编码问题。
- 提供 `AFRLOG` 日志窗口，让用户查看缺失字体、运行时字体映射、当前样式字体、替换结果并进行部分手动替换。
- 提供 WPF 部署器，把不同 AutoCAD 版本的插件 DLL 写入 AutoCAD 注册表自动加载位置。
- 提供 GlyphCore 训练、评估和网页工作台工具，用于本地模型迭代。

当前正式 AutoCAD 命令：

- `AFR`：打开字体配置窗口，保存字体配置，并在当前图纸上执行或重新应用替换。
- `AFRLOG`：打开字体替换日志与手动检查窗口。
- `AFRUNLOAD`：隐藏维护命令，不通过 `CommandMethod` 注册，而是通过 `UnknownCommand` 精确匹配触发卸载和注册表清理。

当前 Debug 相关命令状态：

- `CommandNames.cs` 中仍保留 Debug 条件编译名称 `AFRGLYPHCOREEXPORT` 和 `AFRGLYPHCOREEXPORTSELECT`。
- `src/AutoCAD/AFR.AutoCAD/AFR.AutoCAD.projitems` 会在 Debug 下编译 `GlyphCoreExportCommand` 等调试命令文件。
- 当前各版本 `PluginEntry.cs` 只声明了 `[assembly: CommandClass(typeof(AFR.Commands.AfrCommands))]`。如果 AutoCAD 受 `CommandClass` 限制只扫描该类型，则 Debug 命令可能不会被注册。此点需要真实 CAD Debug 环境确认。
- 旧文档中出现过的 `AFRVIEW`、`AFRINSERT`、`AFRDUMPAPI` 等调试命令，在当前源码中对应 `CommandMethod` 或 `CommandNames` 多为注释状态，不能按可用命令处理。

## 2. 项目背景与设计目标

本项目解决的是工程 DWG 在不同电脑、不同 AutoCAD 版本、不同字体安装环境下打开时常见的字体缺失和文字乱码问题。

典型背景问题：

- 图纸引用了用户本机不存在的 SHX 字体。
- 图纸使用 BigFont 组合，普通替换只改主字体会导致中文仍异常。
- 图纸样式中引用 TrueType 字体，但运行环境没有安装或字体名本地化不同。
- MText 内联字体覆盖了样式字体，即使样式被替换，文字片段仍引用缺失字体。
- DBText 可能在 DWG 读取过程中被错误编码解释，表现为乱码或私用区字符。
- AutoCAD 对缺失字体的加载行为发生在普通命令执行之前，单纯命令层替换不一定能阻止原生缺字提示。

设计目标：

1. 配置一次后，后续打开图纸尽量自动修复。
2. 用户侧操作尽量少，不要求用户理解 SHX、BigFont、编码或模型细节。
3. 运行时尽量不破坏原始图纸结构，只在明确缺失、明确可替换、明确有证据时写回。
4. 多 AutoCAD 版本用同一套共享核心逻辑，版本差异集中在壳项目和平台 Profile。
5. DBText AI 必须本地运行，不依赖在线服务，不把用户图纸上传到外部。
6. 原生 Hook 只作为证据链或字体重定向机制，不能变成不可控的全局副作用。
7. 部署器必须可重复安装、卸载，并尽量只清理自己写入的内容。
8. 训练数据、候选模型、报告和原始 DWG 属于本地资产，默认不进入 Git。

这些目标解释了当前架构为什么分成 AutoCAD 层、Core 层、UI 层、HostIntegration 层、Deployer 层和 GlyphCore 工具链，而不是把所有逻辑写在一个插件项目里。

## 3. 技术栈与运行环境

### 3.1 主要运行环境

- 操作系统：Windows。
- CAD 宿主：AutoCAD 2018 到 AutoCAD 2027。
- CPU 架构：x64。`src/AutoCAD/Directory.Build.props` 中强制 `PlatformTarget=x64`。
- 插件加载方式：当前实际代码使用 AutoCAD 注册表 `Applications\<AppName>` 自动加载机制，不是 `.bundle` 机制。
- UI 技术：
  - 插件内窗口：WPF。
  - 部署器：WPF + `WPF-UI` + `CommunityToolkit.Mvvm`。
  - GlyphCore 工作台：React/Vite 前端 + Python 本地服务。

### 3.2 .NET 和 AutoCAD 版本矩阵

各 AutoCAD 版本壳项目位于 `src/AutoCAD/AFR-ACAD20XX/`，它们共享同一套核心源码，只改变目标框架、AutoCAD.NET 包、注册表路径和平台 Profile。

| 壳项目 | TargetFramework | AutoCAD.NET 包 | CAD Registry Base |
|---|---:|---:|---|
| `AFR-ACAD2018` | `net462` | `AutoCAD.NET=22.0.0` | `R22.0` |
| `AFR-ACAD2019` | `net472` | `AutoCAD.NET=23.0.0` | `R23.0` |
| `AFR-ACAD2020` | `net472` | `AutoCAD.NET=23.1.0` | `R23.1` |
| `AFR-ACAD2021` | `net48` | `AutoCAD.NET=24.0.0` | `R24.0` |
| `AFR-ACAD2022` | `net48` | `AutoCAD.NET=24.1.51000` | `R24.1` |
| `AFR-ACAD2023` | `net48` | `AutoCAD.NET=24.2.0` | `R24.2` |
| `AFR-ACAD2024` | `net48` | `AutoCAD.NET=24.3.0` | `R24.3` |
| `AFR-ACAD2025` | `net8.0-windows` | `AutoCAD.NET=25.0.1` | `R25.0` |
| `AFR-ACAD2026` | `net8.0-windows` | `AutoCAD.NET=25.1.0` | `R25.1` |
| `AFR-ACAD2027` | `net10.0-windows` | `AutoCAD.NET=26.0.0` | `R26.0` |

说明：

- 2018-2024 仍走 .NET Framework。
- 2025-2026 走 .NET 8 Windows。
- 2027 当前配置为 .NET 10 Windows。
- 新增 CAD 版本时不能只复制 csproj，还必须同步平台 Profile、注册表基路径、发布脚本验证、部署器描述符生成和真实 CAD 冒烟测试。

### 3.3 主要 NuGet 依赖

AutoCAD 插件共享依赖在 `src/AutoCAD/Directory.Build.targets` 中集中处理：

- `HandyControl=3.5.1`：插件 WPF UI 控件。
- `Newtonsoft.Json=13.0.3`：JSON 序列化。
- `Microsoft.ML.OnnxRuntime=1.18.0`：GlyphCore 本地 ONNX 推理。
- `System.Text.Encoding.CodePages=8.0.0`：仅 `net8.0-windows` 下引用，用于代码页支持。

部署器依赖在 `src/AFR.Deployer/AFR.Deployer.csproj`：

- `CommunityToolkit.Mvvm=8.4.2`
- `WPF-UI=4.2.1`
- `System.Management=10.0.7`
- `Newtonsoft.Json=13.0.3`

GlyphCore 工具链使用 Python、Node.js、React/Vite、LightGBM、ONNX 相关工具。具体依赖以 `AFR.GlyphCore/tools/README.md`、训练脚本和前端 `package.json` 为准。

## 4. 解决方案结构和目录说明

解决方案入口是 `CADFontAutoReplace.slnx`。它包含以下主要分组：

- `Repository`
  - `README.md`
  - `Version.props`
  - `Directory.Build.props`
  - `Directory.Packages.props`
  - `global.json`
- `Src`
  - `src/AFR.Core/AFR.Core.shproj`
  - `src/AFR.Polyfills/AFR.Polyfills.shproj`
  - `src/AFR.HostIntegration/AFR.HostIntegration.shproj`
  - `src/AFR.UI/AFR.UI.shproj`
  - `src/AFR.Deployer/AFR.Deployer.csproj`
  - `src/AutoCAD/AFR.AutoCAD/AFR.AutoCAD.shproj`
  - `src/AutoCAD/AFR-ACAD2018/AFR-ACAD2018.csproj`
  - `src/AutoCAD/AFR-ACAD2019/AFR-ACAD2019.csproj`
  - `src/AutoCAD/AFR-ACAD2020/AFR-ACAD2020.csproj`
  - `src/AutoCAD/AFR-ACAD2021/AFR-ACAD2021.csproj`
  - `src/AutoCAD/AFR-ACAD2022/AFR-ACAD2022.csproj`
  - `src/AutoCAD/AFR-ACAD2023/AFR-ACAD2023.csproj`
  - `src/AutoCAD/AFR-ACAD2024/AFR-ACAD2024.csproj`
  - `src/AutoCAD/AFR-ACAD2025/AFR-ACAD2025.csproj`
  - `src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj`
  - `src/AutoCAD/AFR-ACAD2027/AFR-ACAD2027.csproj`
- `AFR GlyphCore`
  - `AFR.GlyphCore/models/AFR.GlyphCore.Model.onnx`
  - `AFR.GlyphCore/models/AFR.GlyphCore.ModelManifest.json`
  - `AFR.GlyphCore/schemas/`
  - `AFR.GlyphCore/tools/`
- `Docs`
  - `docs/user-manual.md`
  - `docs/developer-guide-beginner.md`
  - `docs/developer-guide-advanced.md`
  - `docs/git-branch-guidelines.md`
  - `docs/debugging/DBText-Repair-Model.md`
- `Tools`
  - `tools/Publish-ReleaseAssets.ps1`

### 4.1 顶层配置文件

- `Version.props`
  - 当前插件版本和构建号来源。
  - 发布资产命名、DLL 元数据和部署器版本比较都依赖它。
- `Directory.Build.props`
  - 设置 `Version`、`AssemblyVersion`、`FileVersion`、`InformationalVersion`。
  - 设置 `ArtifactsPath=artifacts`。
- `Directory.Packages.props`
  - 当前没有启用 NuGet Central Package Management。
- `global.json`
  - GitHub Actions 和本地构建用于确定 .NET SDK。
- `.gitignore`
  - 忽略构建输出、训练数据、本地 DWG、GlyphCore 候选包、报告、前端缓存等。
  - `AFR.GlyphCore/models/AFR.GlyphCore.Model.onnx` 和 `AFR.GlyphCore/models/AFR.GlyphCore.ModelManifest.json` 是当前可跟踪的构建输入，不应误加入忽略规则。

### 4.2 AutoCAD 共享项目和版本壳

`src/AutoCAD/AFR.AutoCAD/AFR.AutoCAD.shproj` 是 AutoCAD 插件共享源码。它不是独立输出 DLL，而是被每个 `AFR-ACAD20XX.csproj` 引入。

各版本壳项目负责：

- 声明对应 AutoCAD.NET 包版本。
- 声明目标框架。
- 声明 `CadBrand`、`CadVersion`、`CadRegistryBasePath`、`AppName`、`CadDisplayName`。
- 编译共享项目源码。
- 在 Release 构建时生成 `.cad.json` 描述符，供部署器内嵌。

这种设计的原因是 AutoCAD .NET API、运行时 CLR、注册表路径和原生二进制偏移随版本变化，但业务逻辑应尽量保持一致。

### 4.3 GlyphCore 目录

`AFR.GlyphCore` 是 DBText AI 修复和训练相关目录：

- `AFR.GlyphCore/models/`
  - 当前发布使用的 ONNX 模型和模型清单。
- `AFR.GlyphCore/schemas/`
  - 特征 schema，例如当前运行时常量对应 `dbtext-ai-features-v7`。
- `AFR.GlyphCore/tools/training/`
  - 特征构建、候选模型训练、评估测试脚本。
- `AFR.GlyphCore/tools/workbench/`
  - 本地网页工作台后端和 React 前端。
- `AFR.GlyphCore/datasets/`、`candidates/`、`reports/` 等
  - 本地训练数据和报告，通常被 `.gitignore` 排除。

## 5. 核心模块职责

### 5.1 `AFR.Core`

路径：`src/AFR.Core/`

职责：

- 定义跨 AutoCAD 层和 UI 层共享的核心模型、常量和服务。
- 提供 `CommandNames`，统一命令字符串。
- 提供平台抽象，例如 `PlatformManager`、`AutoCADPlatformProfile`。
- 提供字体、BigFont、系统字体索引、SHX 分析等核心逻辑。
- 提供 GlyphCore DBText AI 修复核心逻辑。

关键入口：

- `src/AFR.Core/CommandNames.cs`
- `src/AFR.Core/Services/ConfigService.cs`
- `src/AFR.Core/Services/RegistryService.cs`
- `src/AFR.Core/Services/LogService.cs`
- `src/AFR.Core/Services/EmbeddedFontExtractor.cs`
- `src/AFR.Core/GlyphCore/TextRepair/`
- `src/AFR.Core/GlyphCore/Native/`

修改建议：

- 改命令名时先看 `CommandNames.cs`，再同步 AutoCAD 命令注册、日志文本、文档和部署说明。
- 改注册表结构时先看 `ConfigService`、`AppInitializer`、`PluginDeployer`、`PluginUninstaller`，不能只改一侧。
- 改 DBText AI 时必须同时看运行时 feature extractor、schema、训练脚本、模型 manifest 和嵌入资源配置。

### 5.2 `AFR.AutoCAD`

路径：`src/AutoCAD/AFR.AutoCAD/`

职责：

- 承接 AutoCAD API。
- 注册插件生命周期。
- 注册 AutoCAD 命令。
- 处理文档事件、Idle 延迟执行、数据库事务、Editor 输出。
- 安装和卸载原生 Hook。
- 执行字体检测、字体替换、MText 修复和 DBText 修复主流程。

关键入口：

- `PluginEntryBase.cs`
- `Commands/AfrCommands.cs`
- `Commands/AfrUnloadCommand.cs`
- `Services/AppInitializer.cs`
- `Services/ExecutionController.cs`
- `Services/DocumentContextManager.cs`
- `FontMapping/FontDetector.cs`
- `FontMapping/FontReplacer.cs`
- `FontMapping/LdFileHook.cs`
- `MText/MTextInlineFontScanner.cs`
- `MText/MTextInlineFontReplacer.cs`
- `MText/MTextFontParser.cs`

修改建议：

- 改插件启动行为先看 `PluginEntryBase`。
- 改 `AFR` 命令行为先看 `AfrCommands.AfrCommand()`。
- 改自动执行顺序先看 `ExecutionController.Execute()`。
- 改 Hook 行为必须先确认目标 CAD 版本对应 `PlatformProfile` 是否支持。

### 5.3 `AFR.UI`

路径：`src/AFR.UI/`

职责：

- 插件内 WPF 窗口和 ViewModel。
- 字体选择窗口。
- 字体替换日志窗口。
- MText 调试窗口和插入窗口。
- 高 DPI 窗口定位辅助。

关键入口：

- `FontSelection/FontSelectionWindow.xaml`
- `FontSelection/FontSelectionViewModel.cs`
- `FontLog/FontReplacementLogWindow.xaml`
- `FontLog/FontReplacementLogViewModel.cs`
- `MTextEditor/MTextEditorWindow.xaml`
- `WindowPositionHelper.cs`

修改建议：

- 新 UI 功能优先使用 MVVM，不要把业务逻辑塞进 code-behind。
- 窗口必须考虑 AutoCAD 多显示器、高 DPI 和宿主窗口位置。
- 视觉风格应对齐现有插件窗口和部署器，不要引入完全不同的设计语言。

### 5.4 `AFR.HostIntegration`

路径：`src/AFR.HostIntegration/`

职责：

- 提供宿主集成用资源和工具。
- 当前包含内置 SHX 字体资源，例如 `ming.shx`、`tssdchn.shx`。
- 插件侧和部署器侧都会使用这些字体资源。

修改建议：

- 替换或新增内置字体时必须同时验证插件侧部署、部署器侧部署、发布资产 `Fonts.zip` 和字体默认配置。
- 不要在安装时覆盖用户已存在的同名字体，当前设计是只补齐不存在的字体。

### 5.5 `AFR.Deployer`

路径：`src/AFR.Deployer/`

职责：

- 独立 WPF 安装器。
- 扫描本机 AutoCAD 安装和 profile。
- 把嵌入的 `AFR-ACAD20XX.dll` 写入部署目录。
- 写入 AutoCAD 注册表自动加载项。
- 部署内置字体到 CAD Fonts 目录。
- 卸载时只删除自己安装的 DLL 和自己写入的注册表项。

关键入口：

- `AFR.Deployer.csproj`
- `app.manifest`
- `ViewModels/MainViewModel.cs`
- `Services/CadDescriptor.cs`
- `Services/CadRegistryScanner.cs`
- `Services/PluginDeployer.cs`
- `Services/PluginUninstaller.cs`
- `Services/EmbeddedFontPatcher.cs`
- `Services/AwsHideableDialogPatcher.cs`
- `Services/StatusResolver.cs`

设计重点：

- `app.manifest` 中 `requestedExecutionLevel=requireAdministrator`，部署器需要 UAC。
- 部署器通过嵌入的 `.cad.json` 识别各 AutoCAD 版本 DLL。
- `PluginDeployer` 写注册表时会尽量保留已有用户配置。
- `PluginUninstaller` 通过 `__Owned` 标记清理自己负责的外部注册表值，避免误删用户或其他插件配置。

### 5.6 `AFR.GlyphCore`

路径：`AFR.GlyphCore/`

职责：

- 维护 DBText 修复模型、模型 manifest、特征 schema、训练数据、候选模型、报告和工作台。
- 当前运行时常量使用 `FeatureSchemaVersion="dbtext-ai-features-v7"`，特征数量 `101`。
- 模型文件和 manifest 会在插件 Release 构建时作为嵌入资源打入各版本 DLL。

关键入口：

- `AFR.GlyphCore/models/AFR.GlyphCore.Model.onnx`
- `AFR.GlyphCore/models/AFR.GlyphCore.ModelManifest.json`
- `AFR.GlyphCore/schemas/feature_schema_v7.json`
- `AFR.GlyphCore/tools/README.md`
- `AFR.GlyphCore/tools/workbench/`
- `AFR.GlyphCore/tools/training/`

修改建议：

- 修改模型特征必须同时修改 C# runtime、schema、训练脚本、测试脚本和 manifest。
- 训练数据、本地报告和候选模型默认不提交 Git。
- 工作台生产 UI 不能改成纯 mock 数据页面，必须保留真实 API 支撑。

## 6. AutoCAD 命令入口和执行流程

### 6.1 插件装载入口

各版本壳项目都有 `PluginEntry.cs`，核心形式类似：

```csharp
[assembly: ExtensionApplication(typeof(AFR.PluginEntry))]
[assembly: CommandClass(typeof(AFR.Commands.AfrCommands))]
```

`PluginEntry` 继承 `PluginEntryBase`。实际生命周期逻辑在：

- `src/AutoCAD/AFR.AutoCAD/PluginEntryBase.cs`

`PluginEntryBase.Initialize()` 主要执行：

1. Debug 下初始化 `DiagnosticLogger`。
2. 安装嵌入程序集解析逻辑。
3. 预提取 GlyphCore ONNX Runtime 原生运行时。
4. 安装隐藏 `AFRUNLOAD` 路由。
5. 初始化 `PlatformManager`。
6. 执行 `AppInitializer.Initialize()`。
7. 首次配置场景下返回，不继续自动执行，要求重启 CAD。
8. 非首次配置场景下安装字体 Hook 和 GlyphCore Native Decode Hook。
9. 安装 SHX 数学符号显示 Overrule。
10. 预热字体索引。
11. 注册文档事件。
12. 对当前文档排队，在 AutoCAD Idle 阶段执行自动流程。

为什么需要 Idle 阶段：

- AutoCAD 文档事件触发时，数据库和 Editor 状态可能还未稳定。
- 字体检测、事务写回、Regen、Hook 证据关联都依赖文档上下文完整。
- 通过队列和 `Application.Idle` 延迟执行，可以减少启动阶段的时序问题。

### 6.2 首次运行流程

首次运行或注册表配置未完成时：

1. `AppInitializer.Initialize()` 写入当前 profile 的插件注册表项。
2. 部署默认字体。
3. 写入默认 `MainFont`、`BigFont`、`TrueTypeFont`、`IsInitialized` 等配置。
4. 设置 AutoCAD 系统变量：
   - `FONTMAP=""`
   - `FONTALT="."`
5. 通过命令行提示用户重启 AutoCAD。
6. `PluginEntryBase.Initialize()` 不继续安装后续 Hook 和自动执行。

这样设计的原因：

- `LdFileHook` 必须在图纸加载前安装才最有效。
- 首次运行时图纸可能已经打开，继续处理会产生半初始化状态。
- 重启后进入稳定路径，字体重定向和自动修复都更可靠。

### 6.3 正式命令 `AFR`

入口：

- `src/AutoCAD/AFR.AutoCAD/Commands/AfrCommands.cs`
- 方法：`AfrCommand()`

执行逻辑：

1. 打开 `FontSelectionWindow`。
2. 用户选择主字体、BigFont、TrueType 字体。
3. 写入 `ConfigService.MainFont`、`ConfigService.BigFont`、`ConfigService.TrueTypeFont`。
4. 设置 `ConfigService.IsInitialized=true`。
5. 更新 `LdFileHook.UpdateConfig()`。
6. 如果 Hook 尚未安装，提示用户重启 CAD。
7. 如果当前文档已有保存的检测结果，则调用 `ReapplyWithNewConfig()` 复用检测结果重新替换。
8. 否则调用 `ExecutionController.Execute()` 走完整检测和替换流程。

为什么 `AFR` 会复用检测结果：

- 打开图纸后的检测结果已经存入 `DocumentContextManager`。
- 用户只改配置时，不需要重新扫描全部上下文即可重新应用字体替换。
- 复用检测结果可以减少大图纸操作成本，同时保持手动配置反馈及时。

### 6.4 正式命令 `AFRLOG`

入口：

- `src/AutoCAD/AFR.AutoCAD/Commands/AfrCommands.cs`
- 方法：`AfrLogCommand()`

执行逻辑：

1. 锁定当前文档。
2. 创建新的 `FontDetectionContext`。
3. 调用 `FontDetector.DetectMissingFonts()` 获取当前缺失字体。
4. 调用 `CollectRuntimeFontMappings()` 获取运行时字体映射。
5. 读取 `DocumentContextManager` 中保存的检测结果、替换后仍缺失结果、MText 修复记录、运行时映射记录。
6. 读取当前样式字体分配。
7. 构造 `FontReplacementLogViewModel`。
8. 打开 `FontReplacementLogWindow`。
9. 用户在日志窗口中手动替换时，调用 `FontReplacer.ReplaceByStyleMapping()`。
10. 替换后重新检测并刷新窗口数据。

`AFRLOG` 的定位：

- 它不是普通文本日志文件查看器，而是一个当前图纸状态诊断窗口。
- 它结合了自动检测结果、当前实时检测结果、运行时映射和用户手动替换能力。

### 6.5 隐藏命令 `AFRUNLOAD`

入口：

- `src/AFR.Core/CommandNames.cs`
- `src/AutoCAD/AFR.AutoCAD/Commands/AfrUnloadCommand.cs`
- `src/AutoCAD/AFR.AutoCAD/PluginEntryBase.cs`

实现方式：

- 不使用 `[CommandMethod]` 注册。
- `PluginEntryBase` 在文档 `UnknownCommand` 事件中精确匹配 `AFRUNLOAD`。
- 匹配后调用 `AfrUnloadCommand.Execute()`。

执行内容：

1. 调用 `PluginEntryBase.Unload()` 卸载 Hook、清理文档状态、重置 scorer、注销事件。
2. 调用 `ConfigService.DeleteAllApplicationKeys()` 删除插件注册表应用键。
3. 输出日志。

为什么隐藏：

- 这是维护命令，不面向普通用户。
- 它会清理注册表配置，风险高于普通命令。
- 通过 UnknownCommand 路由避免暴露在普通命令列表中。

### 6.6 自动执行流程

入口：

- `src/AutoCAD/AFR.AutoCAD/Services/ExecutionController.cs`

执行顺序：

1. 检查文档是否已经执行过：
   - `DocumentContextManager.HasExecuted(document)`
2. 检查配置是否已初始化：
   - `ConfigService.IsInitialized`
3. 锁定当前文档。
4. 创建 `FontDetectionContext`。
5. 检测样式缺失字体：
   - `FontDetector.DetectMissingFonts()`
6. 收集运行时字体映射。
7. 保存检测结果到 `DocumentContextManager`。
8. 替换缺失样式字体：
   - `FontReplacer.ReplaceMissingFonts()`
9. 再次检测仍缺失字体。
10. 清理 stale SHX 引用：
    - `FontReplacer.CleanupStaleShxReferences()`
11. `Editor.Regen()` 刷新显示。
12. 扫描 MText 内联字体：
    - `MTextInlineFontScanner.ScanInlineFonts()`
13. 转换缺失 TrueType 内联字体：
    - `MTextInlineFontReplacer.ConvertMissingTrueTypeToShx()`
14. 从 `LdFileHook.GetRawRedirectLog()` 构造内联修复记录。
15. 保存 MText 修复结果。
16. 执行 DBText AI 修复：
    - `GlyphCoreTextRepairService.Repair()`
17. 根据 DBText 修复结果再次刷新图纸。
18. 输出统计到命令行日志。
19. 标记当前文档已经执行。

这个顺序不能随意调整。字体样式和 MText 修复会改变 DBText 修复前后的上下文，DBText AI 又依赖原生读取阶段和 Hook 证据。因此必须先完成字体基础处理，再进入 DBText AI。

## 7. 字体检测、字体替换、BigFont、MText、DBText 核心业务逻辑

### 7.1 字体检测

入口：

- `src/AutoCAD/AFR.AutoCAD/FontMapping/FontDetector.cs`

核心逻辑：

- 遍历 `TextStyleTable`。
- 跳过 ShapeFile 样式。
- 读取 `TextStyleTableRecord.FileName`、`BigFontFileName` 和 `FontDescriptor`。
- 判断字体类型：
  - SHX：通过文件扩展名和 `ShxFontAnalyzer` 判断主字体或 BigFont。
  - TrueType：通过 `FontDescriptor.TypeFace`、字体文件扩展名、系统字体索引、WPF 本地化字体名等判断。
- 通过 `HostApplicationServices.Current.FindFile()` 判断 AutoCAD 能否找到 SHX 文件。
- 对 `@TrueType` 形式的竖排字体做特殊处理。

为什么跳过 ShapeFile：

- ShapeFile 样式与普通文字样式不同，可能用于符号和形文件。
- 误替换会破坏图纸符号显示。

为什么检测中存在 TrueType 本地化逻辑：

- 中文 Windows 和英文 Windows 上同一字体可能有不同显示名。
- 仅按 `TypeFace` 字符串匹配会误判已经安装的字体为缺失。

### 7.2 样式字体替换

入口：

- `src/AutoCAD/AFR.AutoCAD/FontMapping/FontReplacer.cs`

关键方法：

- `ReplaceMissingFonts()`
- `ReplaceByStyleMapping()`
- `CleanupStaleShxReferences()`

替换策略：

- 缺失 SHX 主字体时，替换为配置的 `MainFont`。
- 缺失 BigFont 时，替换为配置的 `BigFont`。
- 缺失 TrueType 时，替换为配置的 `TrueTypeFont`。
- TrueType 替换会清理 SHX 相关字段，避免一条样式同时保留互相冲突的 SHX 和 TrueType 状态。
- SHX 替换会清理 `FontDescriptor`，再设置 `FileName` 和 BigFont。

为什么要区分 SHX 与 TrueType：

- AutoCAD 的文字样式中 SHX 和 TrueType 使用不同字段表达。
- 混用字段可能导致 UI 看起来已替换，但运行时仍按旧字体加载。

为什么需要二次检测：

- 替换后仍可能因目标字体不存在、路径不可见、CAD 查找路径异常而失败。
- 二次检测可以给 `AFRLOG` 和命令行统计提供真实结果。

### 7.3 BigFont 处理

BigFont 是中文 DWG 中非常关键的字体组合机制。主字体和 BigFont 的判断及替换分散在：

- `FontDetector`
- `FontReplacer`
- `ShxFontAnalyzer`
- `LdFileHook`
- `ConfigService`
- `EmbeddedFontExtractor`

当前默认资源：

- 主 SHX 字体：`ming.shx`
- BigFont：`tssdchn.shx`
- TrueType 默认字体：`宋体`

设计约束：

- BigFont 缺失不能简单替换主字体。
- 主字体和 BigFont 必须分别检测。
- 运行时 Hook 中 `param2` 会影响是主字体还是 BigFont 请求。
- `LdFileHook` 会为 BigFont 请求寻找合适的替代字体。

维护建议：

- 修改默认 BigFont 时必须同时验证：
  - 内置资源是否存在。
  - 插件侧字体部署是否成功。
  - 部署器侧字体部署是否成功。
  - `ConfigService` 默认值是否正确。
  - `chore/Fonts.zip` 是否包含发布所需字体。
  - 真 CAD 中中文文字是否仍可显示。

### 7.4 运行时字体重定向 `LdFileHook`

入口：

- `src/AutoCAD/AFR.AutoCAD/FontMapping/LdFileHook.cs`

职责：

- Hook AutoCAD `acdb` 中的 `ldfile` 字体加载函数。
- 在 AutoCAD 请求缺失字体时返回配置的替代字体路径。
- 对 SHX、BigFont、TrueType 和竖排 TrueType 做不同处理。
- 记录运行时重定向日志，供 `AFRLOG` 展示。

关键约束：

- Hook 必须尽早安装，最好在图纸打开前。
- 直接 Shape 请求会透传，避免破坏形文件。
- 竖排 TrueType `@xxx` 只做运行时映射，不写入普通字体替换日志。
- Hook 中需要防递归，当前有 `_inHook` 等保护。
- 原生指针、字符串缓存和平台 Prologue 大小都跟 AutoCAD 版本相关。

为什么需要 Hook：

- 有些字体加载发生在命令执行前。
- 单纯在 `AFR` 命令里改样式无法阻止 AutoCAD 先弹缺字或先用错误字体加载。
- Hook 解决的是“加载阶段”的问题，而 `FontReplacer` 解决的是“数据库写回”的问题。

重要边界：

- `LdFileHook` 属于字体加载链，不是 DBText AI 的触发证据链。
- DBText AI 依赖的是 `GlyphCoreNativeDecodeEvidenceStore` 和相关 native decode hooks。
- 维护时不要把 `LdFileHook` 的字体重定向日志当成 DBText AI 的强信号。

### 7.5 MText 内联字体处理

入口：

- `src/AutoCAD/AFR.AutoCAD/MText/MTextInlineFontScanner.cs`
- `src/AutoCAD/AFR.AutoCAD/MText/MTextFontParser.cs`
- `src/AutoCAD/AFR.AutoCAD/MText/MTextInlineFontReplacer.cs`

MText 内联字体问题：

- MText 内容中可能包含 `\F...|` 或 `\f...|` 控制片段。
- 这些片段可以覆盖文字样式字体。
- 只替换 TextStyle 不能修复 MText 内部仍引用缺失字体的问题。

当前处理：

- `MTextInlineFontScanner` 遍历模型空间、布局空间和嵌套块中的 MText。
- `MTextFontParser` 解析 MText 内容流，识别主字体、BigFont、参数和终止符。
- `MTextInlineFontReplacer` 将缺失 TrueType 内联字体转换为 SHX 形式。
- 转换目标通常为 `\Fmain,big|`，使用配置主字体和 BigFont。
- 对明显乱码或私用区字体名做跳过处理，避免把异常控制文本误当字体名。

维护建议：

- 修改 MText parser 时必须增加真实样例测试，不能只靠字符串 `Split`。
- MText 控制码允许转义、参数、缺失终止符等复杂情况，简单正则容易破坏内容。
- 写回 MText 后必须真实 CAD 打开验证显示效果。

### 7.6 DBText GlyphCore AI 修复

入口目录：

- `src/AFR.Core/GlyphCore/TextRepair/`
- `src/AFR.Core/GlyphCore/Native/`
- `src/AutoCAD/AFR.AutoCAD/GlyphCore/`

关键类：

- `GlyphCoreTextRepairModels`
- `GlyphCoreTextRepairFeatureExtractor`
- `GlyphCoreTextRepairEmbeddedOnnxScorer`
- `GlyphCoreTextRepairScorerFactory`
- `GlyphCoreTextRepairProblemDetector`
- `GlyphCoreTextRepairCandidateGenerator`
- `GlyphCoreTextRepairDecisionEngine`
- `GlyphCoreTextRepairService`
- `GlyphCoreNativeDecodeEvidenceStore`
- `GlyphCoreNativeDbTextEvidenceProjector`

当前模型约束：

- `FeatureSchemaVersion="dbtext-ai-features-v7"`
- 特征数量：`101`
- 模型资源名：
  - `AFR.GlyphCore.Model.onnx`
  - `AFR.GlyphCore.ModelManifest.json`
- ONNX Runtime 资源：
  - `Microsoft.ML.OnnxRuntime.dll`
  - `onnxruntime.dll`
  - `onnxruntime_providers_shared.dll`

DBText 修复流程概述：

1. 原生 Hook 在 DWG 读取或文本解码相关路径中捕获证据。
2. `GlyphCoreNativeDecodeEvidenceStore` 只在内存中保存对象级、cluster 级、ripple 级和 document-family 级证据。
3. `GlyphCoreNativeDbTextEvidenceProjector` 把 native provenance 投影到托管 DBText 上下文。
4. `GlyphCoreTextRepairService` 扫描非 xref、非 dependent block 中的 DBText。
5. `GlyphCoreTextRepairProblemDetector` 判断是否存在可修复问题。
6. `GlyphCoreTextRepairCandidateGenerator` 生成候选文本。
7. `GlyphCoreTextRepairFeatureExtractor` 提取 101 维特征。
8. `GlyphCoreTextRepairEmbeddedOnnxScorer` 加载嵌入 ONNX 模型并评分。
9. `GlyphCoreTextRepairDecisionEngine` 根据 AI 分数、margin、置信度、native 证据决定是否写回。
10. `GlyphCoreTextRepairService` 写回 `DBText.TextString`，并处理对齐和图形刷新。

强信号原则：

- DBText AI 不能仅凭“看起来像乱码”修复。
- 需要 native decode evidence、编码族不匹配、Hook 命中类型、对象或 cluster 相关性等证据。
- 当前问题检测会在没有强 native 证据、没有对齐 carrier 修复候选时跳过。

AI 决策原则：

- AI 模型是真正的决策层，但它必须在有效触发条件和候选集合内决策。
- 没有 scorer 时返回 `no-ai-score`，不能写回。
- 低置信度、小 margin、AI 选择当前文本、候选不安全时不能写回。
- 存在强 native 证据且满足较低但明确阈值时，可以走保守证据接受路径。

为什么部分特征故意为 0：

- `GlyphCoreTextRepairFeatureExtractor` 中部分 slot 为字体身份和环境稳定性预留或清零。
- 这是为了避免模型过度依赖当前机器、当前字体环境或不稳定身份信息。
- 修改这些特征会改变 schema 语义，必须重新训练和发布模型。

Native Hook 版本边界：

- 当前文档和平台 Profile 表明 AutoCAD 2022-2027 是完整 native decode hook 支持重点。
- AutoCAD 2018-2021 更偏向 fail-closed 或保守路径。
- 具体每个版本的 Hook 支持应以 `src/AutoCAD/AFR.AutoCAD/PlatformProfiles/` 当前代码为准。

## 8. 关键设计原则和禁止随意修改的约束

### 8.1 分层约束

禁止把 AutoCAD API 直接引入 `AFR.Core` 的通用逻辑中，除非该目录已经明确属于 AutoCAD 运行时上下文。原因是 `AFR.Core` 被多个壳项目共享，并承担模型、配置、服务等跨版本逻辑。

推荐边界：

- AutoCAD API、Document、Database、Transaction：放在 `AFR.AutoCAD`。
- 通用模型、配置、日志、GlyphCore 算法：放在 `AFR.Core`。
- WPF 插件窗口：放在 `AFR.UI`。
- 安装器逻辑：放在 `AFR.Deployer`。
- 字体资源和宿主集成资源：放在 `AFR.HostIntegration`。

### 8.2 不能随意修改的行为

以下行为属于项目稳定性约束：

- 不要覆盖用户已有字体文件。
- 不要把训练数据、用户 DWG、候选模型和报告提交到 Git。
- 不要在普通用户运行路径记录额外敏感审计数据。
- 不要把外部在线 AI 服务加入 DBText 运行时。
- 不要在没有 native 强信号时让 DBText AI 仅凭文本外观写回。
- 不要把 `LdFileHook` 当作 DBText AI 触发证据。
- 不要跳过模型 manifest schema 校验。
- 不要让 `FeatureCount` 与模型、schema、训练脚本不一致。
- 不要把 Debug 命令暴露到 Release。
- 不要让部署器卸载时删除用户或其他插件的注册表项。
- 不要在未验证 CAD 真实加载的情况下调整原生 Hook prologue、导出名、偏移或平台 Profile。
- 不要把 `.bundle` 机制写进用户文档，除非代码实际实现了 `.bundle` 生成和部署。

### 8.3 为什么要坚持强证据修复

字体替换和 DBText 修复都直接修改 DWG 数据库。错误写回的成本很高：

- 文字可能从可读变成错误文本。
- 工程标注可能被破坏。
- 用户可能无法察觉错误文本已经被保存。
- CAD 中的显示和数据值可能不一致。

因此本项目宁可跳过不确定修复，也不要为了“看起来智能”扩大自动写回范围。

### 8.4 修改核心流程前的检查清单

修改 `ExecutionController`、`FontDetector`、`FontReplacer`、`LdFileHook`、`GlyphCoreTextRepairService` 前至少检查：

- 是否影响首次运行和重启提示。
- 是否影响 `AFR` 手动配置后重新应用。
- 是否影响 `AFRLOG` 当前状态展示。
- 是否影响 MText 内联字体。
- 是否影响 DBText strong signal。
- 是否影响 AutoCAD 2018-2027 中某个版本壳。
- 是否需要更新部署器、发布脚本或文档。
- 是否需要真实 CAD 打开 DWG 验证。

## 9. 配置、注册表、实例隔离、日志系统设计

### 9.1 注册表自动加载设计

当前实际自动加载机制是 AutoCAD 注册表 `Applications` 项，而不是 `.bundle`。

核心路径形式：

```text
HKCU\<CadRegistryBasePath>\<Profile>\Applications\<AppName>
```

典型值：

- `LOADER`：插件 DLL 路径。
- `LOADCTRLS=2`：AutoCAD 加载控制。
- `MANAGED=1`：托管插件。
- `DESCRIPTION`：描述。
- `PluginVersion`：插件版本。
- `PluginBuildId`：构建号。
- `ConfigSchemaVersion`：配置 schema 版本。
- `MainFont`：主 SHX 字体配置。
- `BigFont`：BigFont 配置。
- `TrueTypeFont`：TrueType 配置。
- `IsInitialized`：是否完成初始化。

写入入口：

- 插件侧：`AppInitializer`
- 运行配置侧：`ConfigService`
- 部署器侧：`PluginDeployer`
- 卸载侧：`PluginUninstaller`

### 9.2 `ConfigService`

路径：

- `src/AFR.Core/Services/ConfigService.cs`

职责：

- 读取当前平台和 profile 下的插件配置。
- 将用户配置写入当前平台所有匹配 profile 的 `Applications\<AppName>`。
- 缓存配置，写入后失效缓存。
- 删除所有插件应用键。

为什么写入所有 profile：

- AutoCAD 同一版本可能有多个 profile。
- 用户在一个 profile 中配置后，切到另一个 profile 时仍期望插件行为一致。
- 部署器也按 profile 维度扫描和安装。

风险：

- Profile 匹配和路径解析必须谨慎，不能写错 AutoCAD 版本或其他插件键。

### 9.3 `AppInitializer`

路径：

- `src/AutoCAD/AFR.AutoCAD/Services/AppInitializer.cs`

职责：

- 插件启动时确保注册表项存在。
- 首次运行时部署默认字体、写默认配置。
- 迁移旧配置 schema。
- 处理部署器预创建但未完全初始化的注册表项。
- 设置 `FONTMAP` 和 `FONTALT`。
- 应用 `AwsHideableDialogPatcher`。
- 仅在 `AFR_EXTERNAL_REGISTRY` 环境变量存在时处理外部注册表配置。

设计原因：

- 部署器和插件都可能参与初始化。
- 不能假设用户总是先使用部署器。
- 注册表缺失时插件也要有自恢复能力。

### 9.4 实例和文档隔离

入口：

- `src/AutoCAD/AFR.AutoCAD/Services/DocumentContextManager.cs`

职责：

- 记录每个文档是否已执行。
- 保存每个文档的检测结果、替换后仍缺失结果、MText 修复记录、运行时字体映射。
- 文档关闭时清理对应状态。

文档键：

- 优先使用 `Database.Filename`。
- 如果文件名不可用，使用 `Document.Name`。

为什么需要文档隔离：

- AutoCAD 可能同时打开多个 DWG。
- 一个文档的检测结果不能污染另一个文档。
- `AFRLOG` 需要展示当前文档状态。

### 9.5 日志系统

#### 命令行日志 `LogService`

路径：

- `src/AFR.Core/Services/LogService.cs`

职责：

- 将信息输出到 AutoCAD Editor 命令行。
- 对日志按 Error、Warning、Info、Statistics 分组。
- 每个文档输出一次插件头部信息。

头部会提示命令：

- `AFR(配置)`
- `AFRLOG(日志)`
- `AFRUNLOAD(卸载命令)`

#### Debug 文件日志 `DiagnosticLogger`

路径：

- `src/AFR.Core/Services/DiagnosticLogger.cs`

行为：

- 仅 Debug 编译有效。
- Release 下方法带 `[Conditional("DEBUG")]` 或为空实现。
- 日志文件形如 `AFR_Diag_*.log`，位于插件 DLL 目录。
- 单文件最大约 10 MB。
- 保留约 7 天。

设计原因：

- Release 环境不应持续写诊断文件。
- Debug 下需要排查 AutoCAD 启动、Hook、注册表和模型加载问题。

## 10. UI 设计目标和高 DPI 注意事项

### 10.1 插件内 WPF UI

主要窗口：

- `FontSelectionWindow`
- `FontReplacementLogWindow`
- `MTextEditorWindow`
- `MTextInsertWindow`，当前仅 Debug 条件编译。

设计目标：

- 适合 AutoCAD 插件场景，不做复杂营销式界面。
- 用户应能快速配置字体、查看替换结果和手动处理异常。
- 窗口应与 AutoCAD 工作流兼容，避免抢焦点、越界、DPI 错乱。

高 DPI 注意：

- 使用 `WindowPositionHelper` 处理窗口位置。
- 不要假设单显示器或 100% 缩放。
- 修改窗口尺寸、弹窗位置和 owner 时必须在高 DPI、多屏幕下验证。

### 10.2 部署器 UI

入口：

- `src/AFR.Deployer/`
- `ViewModels/MainViewModel.cs`

特点：

- 使用 WPF。
- 使用 `CommunityToolkit.Mvvm`。
- 使用 `WPF-UI`。
- `app.manifest` 启用 PerMonitorV2 DPI awareness。
- 需要管理员权限。

部署器 UI 维护原则：

- 安装、卸载按钮必须反映 CAD 运行状态。
- CAD 正在运行或状态忙时不能强行安装卸载。
- 安装目标路径应清晰展示。
- 版本状态必须由 DLL 元数据、注册表和当前部署器版本共同判断。

### 10.3 GlyphCore 网页工作台 UI

入口：

- `AFR.GlyphCore/tools/workbench/`

当前设计方向：

- 生产工作台是 API 支撑的四页签工作流。
- 不应替换为纯静态 mock。
- 页面应是固定高度壳，内部卡片滚动。
- 标注、训练、报告、模拟测试等操作要符合实际模型迭代流程。

维护建议：

- 修改网页 UI 时先启动真实 workbench，确认 `/api/bootstrap` 可用。
- 不要只改前端样式而破坏后端 API 数据结构。
- 训练报告中的手动动作应保留用户确认门槛，尤其是模拟测试和发布候选模型。

## 11. Debug / Release 差异

### 11.1 编译差异

`src/AutoCAD/AFR.AutoCAD/AFR.AutoCAD.projitems` 中 Debug 条件编译包含：

- `GlyphCoreExportCommand`
- `MTextEditorCommand`
- `MTextInsertCommand`
- `ProfileDumpCommand`
- `ShowAwsPathCommand`
- `GenProbeScriptsCommand`
- `DumpDialogApiCommand`

但是当前源码中部分旧 Debug 命令的 `CommandMethod` 或 `CommandNames` 已注释，且版本壳只注册 `AfrCommands`。因此这些命令是否能在真实 Debug CAD 中被发现，需要确认。

### 11.2 日志差异

- Debug：`DiagnosticLogger` 写文件日志。
- Release：`DiagnosticLogger` 不写普通诊断文件。
- 两者都会通过 `LogService` 向 AutoCAD 命令行输出重要信息。

### 11.3 模型和资源差异

Release 构建会嵌入：

- GlyphCore ONNX 模型。
- GlyphCore manifest。
- ONNX Runtime 托管和原生依赖。
- HandyControl、Newtonsoft.Json 等依赖。

Debug 构建也可能嵌入这些资源，具体取决于构建参数和 `Directory.Build.targets`。修改模型路径时使用：

- `GlyphCoreModelPath`
- `GlyphCoreModelManifestPath`
- `GlyphCoreRuntimeDirectory`

### 11.4 维护含义

- 不要用 Debug 成功代表 Release 成功。
- 不要用单一 CAD 版本成功代表所有壳项目成功。
- Debug 命令只能用于开发和训练数据导出，不应写入用户手册作为普通功能。

## 12. 构建、调试、打包、发布流程

### 12.1 本地构建

常用构建命令：

```powershell
dotnet build .\CADFontAutoReplace.slnx -c Debug
dotnet build .\CADFontAutoReplace.slnx -c Release
```

构建输出默认进入：

```text
artifacts\bin\
artifacts\obj\
```

单版本构建示例：

```powershell
dotnet build .\src\AutoCAD\AFR-ACAD2026\AFR-ACAD2026.csproj -c Release
```

### 12.2 AutoCAD 调试

常见调试路径：

1. 构建对应版本壳项目。
2. 启动对应 AutoCAD。
3. 通过部署器安装，或手动 `NETLOAD` 对应 DLL。
4. 使用 `AFR` 配置字体。
5. 重启 CAD，使 Hook 在图纸打开前安装。
6. 打开测试 DWG。
7. 使用 `AFRLOG` 检查检测、替换、运行时映射和剩余缺失。

真实 CAD 调试注意：

- Hook 相关问题必须在 CAD 中验证，普通单元测试无法覆盖。
- 首次运行后不重启，Hook 行为可能不完整。
- 如果命令不存在，先检查注册表 LOADER 和 `CommandClass`。
- 如果 DBText 未写回，先看 `AFRLOG` 和命令行中的 AI 状态、blocked reason。

### 12.3 发布资产生成脚本

入口：

- `tools/Publish-ReleaseAssets.ps1`

主要参数：

- `-SkipPluginBuild`
- `-GlyphCoreModelPath`
- `-GlyphCoreModelManifestPath`
- `-GlyphCoreRuntimeDirectory`

默认模型路径：

- `AFR.GlyphCore\models\AFR.GlyphCore.Model.onnx`
- `AFR.GlyphCore\models\AFR.GlyphCore.ModelManifest.json`

脚本行为：

1. 自动发现 `src\AutoCAD\AFR-ACAD*\*.csproj`。
2. 从 `Version.props` 读取版本。
3. 构建每个 AutoCAD 插件 Release。
4. 验证每个版本的 DLL 和 `.cad.json`。
5. `dotnet publish` 部署器。
6. 将部署器复制为：
   - `artifacts\ReleaseAssets\AFR-Deployer_v<version>.exe`
7. 将各版本 DLL 打包为：
   - `artifacts\ReleaseAssets\AFR-DLL_v<version>.zip`
8. 复制字体包：
   - `artifacts\ReleaseAssets\Fonts.zip`

当前版本示例：

```text
artifacts\ReleaseAssets\AFR-Deployer_v9.1.0.exe
artifacts\ReleaseAssets\AFR-DLL_v9.1.0.zip
artifacts\ReleaseAssets\Fonts.zip
```

注意：

- 发布脚本当前使用 `v$ReleaseVersion`，支持 `9.1.0` 形式。
- GitHub Release workflow 中存在版本格式校验逻辑，当前脚本片段要求 `^\d+\.\d+$`，这与当前 `Version.props` 的 `9.1.0` 形式存在冲突。该问题应列为已知风险并优先修复或确认。

### 12.4 `.cad.json` 描述符

生成入口：

- `src/AutoCAD/Directory.Build.targets`
- Target：`EmitCadDescriptorJson`

生成条件：

- Release 构建。
- 壳项目设置了 `CadBrand`、`CadVersion`、`CadRegistryBasePath`。

内容包括：

- `brand`
- `version`
- `displayName`
- `registryBasePath`
- `appName`
- `embeddedResourceKey`

部署器通过这些描述符识别嵌入 DLL 对应的 AutoCAD 版本。新增 CAD 版本时如果 `.cad.json` 不生成，部署器无法正确识别。

### 12.5 部署器发布

`src/AFR.Deployer/AFR.Deployer.csproj` 当前配置：

- `TargetFramework=net10.0-windows`
- `OutputType=WinExe`
- `RuntimeIdentifier=win-x64`
- `SelfContained=true`
- `PublishSingleFile=true`
- `ApplicationManifest=app.manifest`
- `AssemblyName=AFR-Deployer`

部署器会嵌入：

- `artifacts\bin\AFR-ACAD*\release\AFR-ACAD*.dll`
- `artifacts\bin\AFR-ACAD*\release\AFR-ACAD*.cad.json`

因此发布部署器前必须先构建各 AutoCAD 插件 Release。

## 13. Autoloader `.bundle` 部署机制

当前仓库未确认存在 `.bundle` 或 `PackageContents.xml` 生成机制。根据当前源码，实际部署和自动加载机制为 AutoCAD 注册表 `Applications` 自动加载项：

```text
HKCU\<CadRegistryBasePath>\<Profile>\Applications\<AppName>
```

关键值为：

- `LOADER`
- `LOADCTRLS`
- `MANAGED`
- `DESCRIPTION`

部署入口：

- 插件侧：`AppInitializer`
- 部署器侧：`PluginDeployer`

卸载入口：

- `PluginUninstaller`
- 隐藏命令 `AFRUNLOAD`

维护结论：

- 不要在当前用户文档中声称项目使用 `.bundle`，除非后续实际实现。
- 如果未来要引入 Autodesk Autoloader `.bundle`，必须新增：
  - `PackageContents.xml` 生成逻辑。
  - 各 CAD 版本 DLL 的组件条目。
  - 与现有注册表安装机制的迁移策略。
  - 部署器 UI 和卸载逻辑。
  - CI 发布资产变化。
  - 真实 CAD 加载测试。

## 14. Git 分支、PR、CI/CD 和发布规则

### 14.1 分支规则

参考：

- `docs/git-branch-guidelines.md`
- `.github/workflows/branch-policy-check.yml`

当前规则概览：

- `main`：稳定发布分支。
- `test`：集成测试分支。
- 功能分支建议使用：
  - `feature/...`
  - `bugfix/...`
  - `docs/...`
  - `refactor/...`
  - `perf/...`
  - `chore/...`

`branch-policy-check.yml` 约束：

- `main` 只接受来自 `test` 的合并。
- 外部 fork 不能直接合并到 `main`。
- 可信开发者可以绕过部分 source 限制。

### 14.2 PR 自动化

参考：

- `.github/workflows/pr-auto-test.yml`

行为：

- 对非可信开发者的分支，自动创建或更新到 `test` 的 PR。
- 使用 DeepSeek 生成 PR 标题和正文。
- 可信开发者跳过自动 PR 流程。

维护注意：

- 不要把敏感信息写入 PR 自动生成提示。
- 如果 PR 自动化失败，应先看 GitHub token、DeepSeek API 配置和分支权限。

### 14.3 Release workflow

参考：

- `.github/workflows/release-build.yml`

触发条件：

- push 到 `main`，且变更命中：
  - `Version.props`
  - `chore/Fonts.zip`
  - `tools/Publish-ReleaseAssets.ps1`
  - `src/**`
  - workflow 自身
- 手动 `workflow_dispatch`

关键流程：

1. 通过 GitHub REST 下载源码。
2. 检查提交必须关联已关闭且合并到 `main` 的 PR。
3. 根据 `global.json` 安装 .NET SDK。
4. 读取 `Version.props`。
5. 执行 `tools/Publish-ReleaseAssets.ps1`。
6. 验证发布资产。
7. 使用 DeepSeek 生成 Release Notes。
8. 创建 GitHub Release 并上传资产。

已知风险：

- workflow 当前版本格式校验看起来要求 `X.Y`，但 `Version.props` 当前为 `9.1.0`，发布脚本也生成 `v9.1.0`。应优先确认并修复 workflow 校验，否则 release 可能失败。

## 15. 常见问题排查

### 15.1 AutoCAD 中没有 `AFR` 命令

优先检查：

1. 注册表 `Applications\<AppName>` 是否存在。
2. `LOADER` 是否指向当前版本 DLL。
3. DLL 路径是否存在，是否被杀毒软件隔离。
4. DLL 是否对应当前 AutoCAD 版本。
5. `PluginEntry.cs` 是否包含正确 `ExtensionApplication` 和 `CommandClass`。
6. AutoCAD 命令行是否有加载异常。

可能原因：

- 部署到了错误 CAD 版本。
- CAD 正在运行时安装，未重启。
- 注册表写入到错误 profile。
- Release 资产缺少对应版本 DLL。

### 15.2 首次配置后字体仍未自动替换

优先确认：

1. 是否已运行 `AFR` 并保存配置。
2. 是否重启 AutoCAD。
3. `IsInitialized` 是否为 `1`。
4. `FONTMAP` 是否已清空。
5. `FONTALT` 是否为 `.`。
6. `LdFileHook` 是否在图纸打开前安装。

设计上首次配置后要求重启，这是正常行为。

### 15.3 字体替换后仍缺字

检查：

1. 配置的 `MainFont` 和 `BigFont` 是否真实存在于 CAD Fonts 路径。
2. 部署器是否成功复制 `ming.shx` 和 `tssdchn.shx`。
3. `AFRLOG` 中替换后仍缺失列表。
4. 是否为 ShapeFile 样式，当前会跳过。
5. 是否为 MText 内联字体覆盖。
6. 是否为竖排 TrueType `@xxx` 运行时映射。

### 15.4 MText 没有修复

检查：

1. MText 内容是否真的包含内联字体控制码。
2. `MTextFontParser` 是否能解析该格式。
3. 字体名是否被判定为缺失。
4. 是否因为乱码字体名或私用区字符被保护性跳过。
5. 转换后是否执行了 `Editor.Regen()`。

### 15.5 DBText AI 没有写回

检查命令行或调试日志中的 blocked reason：

- `no-ai-score`：模型或 scorer 不可用。
- `ai-selected-current`：AI 选择保持当前文本。
- `low-confidence`：置信度不足。
- `low-margin`：候选分数差距不足。
- `no-strong-native-evidence`：没有足够强的 native 证据。
- `unsafe-candidate`：候选不安全。

进一步检查：

1. `AFR.GlyphCore.Model.onnx` 是否嵌入。
2. `AFR.GlyphCore.ModelManifest.json` 是否嵌入。
3. manifest 中 `featureSchemaVersion` 是否等于 `dbtext-ai-features-v7`。
4. runtime 特征数量是否仍为 101。
5. ONNX Runtime 原生 DLL 是否成功提取。
6. 当前 CAD 版本的 native decode hooks 是否支持。
7. 测试 DWG 是否真的触发了 Hook 证据。

### 15.6 部署器不能安装或卸载

检查：

1. 部署器是否以管理员权限运行。
2. AutoCAD 是否正在运行。
3. `MainViewModel` 是否判定 CAD busy。
4. 目标路径是否可写。
5. 注册表是否可写。
6. 版本描述符 `.cad.json` 是否嵌入。
7. 对应 DLL 是否嵌入。

### 15.7 发布资产缺失

检查：

1. `tools/Publish-ReleaseAssets.ps1` 是否成功构建所有 `AFR-ACAD*` 项目。
2. `artifacts\bin\AFR-ACAD20XX\release\` 下是否有 DLL。
3. 同目录是否有 `.cad.json`。
4. `chore\Fonts.zip` 是否存在。
5. 部署器 publish 是否成功。
6. workflow 版本格式校验是否通过。

### 15.8 Debug 命令不可用

当前已知需要确认：

- Debug 命令文件被条件编译进入共享项目。
- 但版本壳 `CommandClass` 当前只声明 `AfrCommands`。
- 如果 AutoCAD 只扫描 `CommandClass` 指定类型，则 Debug 命令不会注册。

处理建议：

1. 在真实 Debug CAD 中确认命令是否存在。
2. 如果不可用，决定是补充 `CommandClass` 注册，还是移除过期 Debug 命令文档。
3. 不要把未确认可用的 Debug 命令写入正式用户文档。

## 16. 已知风险

### 16.1 Release workflow 版本格式冲突

`Version.props` 当前为 `9.1.0`，发布脚本按 `v9.1.0` 生成资产；但 `.github/workflows/release-build.yml` 中存在看起来要求 `X.Y` 的正则校验。此处可能导致 GitHub Release 失败。

建议优先处理：

- 确认项目版本格式应为 `X.Y` 还是 `X.Y.Z`。
- 同步 `Version.props`、发布脚本、workflow、README 和 Release Notes 规则。

### 16.2 Debug 命令注册状态不一致

当前 Debug 命令文件和命令名部分存在，但 `CommandClass` 只注册 `AfrCommands`。真实命令可见性需要确认。

风险：

- 开发者以为 `AFRGLYPHCOREEXPORT` 可用，但 CAD 中不可用。
- 训练数据导出流程被阻塞。

### 16.3 `GlyphCoreRuntimeExtractor` 当前调用状态待确认

`src/AFR.Deployer/Services/GlyphCoreRuntimeExtractor.cs` 存在，但当前搜索未确认部署器主流程调用它。插件侧有运行时预提取逻辑。部署器侧是否应预提取 ONNX Runtime，需要确认。

### 16.4 Native Hook 对 CAD 版本高度敏感

`LdFileHook` 和 GlyphCore native decode hooks 依赖不同 AutoCAD 版本的 DLL、导出名、prologue 大小和内存结构。新增版本或升级 AutoCAD.NET 包时，即使编译通过，也可能运行时 Hook 失效。

### 16.5 2018-2021 DBText AI 能力边界

当前架构中较新版本 CAD 的 native decode evidence 支持更完整，2018-2021 更倾向保守或 fail-closed。不要向用户承诺所有版本 DBText AI 行为完全一致。

### 16.6 训练工具和运行时 schema 必须同步

运行时当前使用 `dbtext-ai-features-v7` 和 101 维特征。训练脚本、schema、manifest、测试脚本和 C# 特征提取器任何一处不同步，都可能出现：

- 模型加载失败。
- `no-ai-score`。
- 模型看似加载但评分语义错误。
- CAD 运行时不写回或误写回。

### 16.7 `.bundle` 机制未实现

如果未来维护者按 Autodesk Autoloader `.bundle` 文档处理本项目，会与当前注册表部署机制不一致。除非实际新增 `.bundle` 生成和部署代码，否则不要迁移用户安装说明。

## 17. 后期维护建议

### 17.1 修改字体检测逻辑

优先查看：

- `FontDetector.cs`
- `ShxFontAnalyzer`
- `SystemFontIndex`
- `ConfigService`
- `AFRLOG` ViewModel

验证：

- SHX 缺失。
- BigFont 缺失。
- TrueType 缺失。
- 已安装 TrueType 本地化名称。
- ShapeFile 样式跳过。
- 竖排 TrueType。

### 17.2 修改字体替换逻辑

优先查看：

- `FontReplacer.cs`
- `ExecutionController.cs`
- `AfrCommands.ReapplyWithNewConfig()`
- `DocumentContextManager.cs`

验证：

- 自动执行替换。
- `AFR` 改配置后重新应用。
- `AFRLOG` 手动替换。
- 替换后二次检测。
- CAD 保存、关闭、重新打开。

### 17.3 修改 `LdFileHook`

优先查看：

- `LdFileHook.cs`
- `PlatformProfiles`
- `PluginEntryBase.Initialize()`
- `PluginEntryBase.Unload()`

验证：

- Hook 安装时机。
- Hook 卸载。
- 缺失 SHX。
- 缺失 BigFont。
- 缺失 TrueType。
- 竖排 TrueType。
- AutoCAD 2022-2027 真实打开测试。

### 17.4 修改 DBText AI

优先查看：

- `GlyphCoreTextRepairFeatureExtractor`
- `GlyphCoreTextRepairProblemDetector`
- `GlyphCoreTextRepairCandidateGenerator`
- `GlyphCoreTextRepairDecisionEngine`
- `GlyphCoreTextRepairService`
- `GlyphCoreNativeDecodeEvidenceStore`
- `GlyphCoreNativeDbTextEvidenceProjector`
- `AFR.GlyphCore/schemas/feature_schema_v7.json`
- `AFR.GlyphCore/tools/training/`
- `AFR.GlyphCore/models/AFR.GlyphCore.ModelManifest.json`

必须同步：

- C# runtime feature count。
- schema 文件。
- Python feature builder。
- 训练脚本。
- 候选模型 gate。
- manifest。
- 嵌入资源。
- 文档。

### 17.5 新增 AutoCAD 版本

步骤建议：

1. 新增 `src/AutoCAD/AFR-ACAD20XX/` 壳项目。
2. 设置正确 `TargetFramework` 和 `AutoCAD.NET` 包。
3. 设置 `CadBrand`、`CadVersion`、`CadRegistryBasePath`、`CadDisplayName`、`AppName`。
4. 新增或更新 `PluginEntry.cs`。
5. 更新 `PlatformProfiles`。
6. 验证 `EmitCadDescriptorJson` 输出 `.cad.json`。
7. 验证部署器嵌入 DLL 和 `.cad.json`。
8. 更新 README、用户手册、开发文档。
9. 在真实 CAD 中验证 `AFR`、`AFRLOG`、自动加载、字体替换、Hook 和 DBText。

### 17.6 修改部署器

优先查看：

- `MainViewModel.cs`
- `CadRegistryScanner.cs`
- `CadDescriptor.cs`
- `PluginDeployer.cs`
- `PluginUninstaller.cs`
- `EmbeddedFontPatcher.cs`
- `StatusResolver.cs`

验证：

- 未安装状态。
- 已安装同版本。
- 已安装旧版本。
- CAD 正在运行。
- 多 profile。
- 卸载后注册表清理范围。
- 字体存在时不覆盖。
- 非系统盘默认路径。

### 17.7 修改发布流程

优先查看：

- `Version.props`
- `tools/Publish-ReleaseAssets.ps1`
- `.github/workflows/release-build.yml`
- `.github/workflows/branch-policy-check.yml`
- `.github/workflows/pr-auto-test.yml`

验证：

- 本地 `Publish-ReleaseAssets.ps1` 成功。
- ReleaseAssets 三个资产存在。
- 每个 DLL 都有 `.cad.json`。
- 部署器能扫描嵌入描述符。
- GitHub workflow 版本校验通过。
- Release tag 和资产命名一致。

## 18. 给未来 AI 的阅读与修改指南

### 18.1 推荐阅读顺序

AI 接手任务时按以下顺序读：

1. 用户当前请求。
2. `PROJECT_HANDOVER.md`。
3. `.github/copilot-instructions.md`，如果存在。
4. `README.md`。
5. 与任务直接相关的代码入口。
6. 相关构建脚本或 workflow。
7. 必要时再查文档和训练工具。

不要先全仓库盲改，也不要根据旧记忆直接改代码。

### 18.2 禁止事项

AI 禁止：

- 编造不存在的命令、模块、脚本或发布资产。
- 把未确认可用的 Debug 命令写成正式功能。
- 修改训练 schema 却不更新 runtime 和模型 manifest。
- 把本地训练数据加入 Git。
- 删除或回滚用户未要求处理的工作区改动。
- 用字符串猜测代替 MText parser。
- 在没有强证据时扩大 DBText 自动写回范围。
- 把 `LdFileHook` 和 DBText native decode evidence 混为一谈。
- 在部署器卸载逻辑中扩大删除范围。
- 跳过真实 CAD 验证就声明 Hook 修改成功。
- 在不了解版本矩阵的情况下只修某一个壳项目。

### 18.3 推荐修改流程

1. 先确认任务属于哪条链路：
   - 字体检测。
   - 字体替换。
   - Hook。
   - MText。
   - DBText AI。
   - UI。
   - 部署器。
   - 发布流程。
2. 找到本文档对应修改入口。
3. 用 `rg` 搜真实类名、命令名、资源名。
4. 只改必要文件。
5. 如果碰到未跟踪或已修改文件，先判断是否用户改动，不要回滚。
6. 对核心链路增加或更新最小必要验证。
7. 运行能在当前环境执行的构建或测试。
8. 对需要真实 CAD 的验证，明确标注未执行或待确认。
9. 更新文档，尤其是命令、发布资产、schema、版本矩阵变化。

### 18.4 AI 修改前检查清单

修改前：

- 是否读了当前相关代码，而不是只靠记忆？
- 是否确认了当前命令是否真实注册？
- 是否确认了 Debug/Release 差异？
- 是否确认了 AutoCAD 版本差异？
- 是否有本地 dirty worktree 需要避开？
- 是否会影响用户数据或 DWG 写回？

修改后：

- 是否能编译？
- 是否影响 `.cad.json` 生成？
- 是否影响 ReleaseAssets？
- 是否需要真实 CAD 验证？
- 是否需要同步 README、docs、copilot instructions 或本文档？
- 是否有“待确认”必须写清楚？

## 19. 当前遗留事项和未来计划

### 19.1 优先确认事项

1. 修复或确认 Release workflow 版本格式规则。
   - 当前 `Version.props` 是 `9.1.0`。
   - 本地发布脚本支持 `v9.1.0`。
   - GitHub workflow 看起来要求 `X.Y`。

2. 确认 Debug 命令注册机制。
   - `CommandClass` 当前只注册 `AfrCommands`。
   - Debug 命令是否可被 AutoCAD 发现需要真实环境验证。

3. 确认 `GlyphCoreRuntimeExtractor` 是否应接入部署器主流程。
   - 当前类存在。
   - 当前未确认调用点。

4. 确认 `.github/copilot-instructions.md` 是否应纳入 Git。
   - 该文件对长期维护很重要。
   - 当前工作区状态显示它可能未被跟踪。

5. 明确是否未来需要 `.bundle` 部署。
   - 当前实际机制是注册表。
   - 如果引入 `.bundle`，需要完整迁移设计。

### 19.2 中期维护建议

- 为 MText parser 增加更多样例测试。
- 为 `FontDetector` 的 TrueType 本地化判断增加回归样例。
- 为部署器多 profile 注册表写入和卸载增加自动化测试。
- 为 Release workflow 增加本地等效验证脚本。
- 将真实 CAD 冒烟测试清单文档化。
- 对 2018-2021 与 2022-2027 的 GlyphCore 能力差异形成明确用户说明。

### 19.3 长期计划

- 继续保持 GlyphCore 本地闭环模型，不引入在线依赖。
- 持续收敛 DBText AI 的强信号触发条件，避免外观启发式扩大误修。
- 保持部署器可回滚、可卸载、可解释。
- 保持 UI 简洁，服务 CAD 工程流，不做复杂低频功能堆叠。
- 对新 AutoCAD 版本建立固定接入清单。
- 定期同步 `README.md`、开发文档、本文档和仓库 AI 指令，避免长期漂移。

## 20. 快速修改入口索引

| 目标 | 优先查看 |
|---|---|
| 改正式命令名 | `src/AFR.Core/CommandNames.cs`、`AfrCommands.cs` |
| 改插件启动逻辑 | `PluginEntryBase.cs`、`AppInitializer.cs` |
| 改自动执行顺序 | `ExecutionController.cs` |
| 改字体检测 | `FontDetector.cs`、`SystemFontIndex`、`ShxFontAnalyzer` |
| 改字体替换 | `FontReplacer.cs`、`AfrCommands.ReapplyWithNewConfig()` |
| 改运行时字体重定向 | `LdFileHook.cs`、`PlatformProfiles` |
| 改 MText 内联字体 | `MTextInlineFontScanner.cs`、`MTextFontParser.cs`、`MTextInlineFontReplacer.cs` |
| 改 DBText AI 触发 | `GlyphCoreTextRepairProblemDetector`、`GlyphCoreNativeDecodeEvidenceStore` |
| 改 DBText AI 候选 | `GlyphCoreTextRepairCandidateGenerator` |
| 改 DBText AI 决策 | `GlyphCoreTextRepairDecisionEngine` |
| 改 DBText AI 特征 | `GlyphCoreTextRepairFeatureExtractor`、`AFR.GlyphCore/schemas/`、训练脚本 |
| 改插件 UI | `src/AFR.UI/` |
| 改部署器 | `src/AFR.Deployer/` |
| 改发布资产 | `tools/Publish-ReleaseAssets.ps1` |
| 改 CI 发布 | `.github/workflows/release-build.yml` |
| 新增 CAD 版本 | `src/AutoCAD/AFR-ACAD20XX/`、`PlatformProfiles`、`Directory.Build.targets` |

## 21. 维护者最终提醒

这个项目的难点不在普通 C# 语法，而在多个边界同时成立：

- AutoCAD 多版本边界。
- .NET Framework 与现代 .NET 边界。
- 注册表自动加载边界。
- 字体加载 Hook 与数据库写回边界。
- MText 控制码边界。
- DBText native evidence 与 AI 决策边界。
- 本地训练资产与发布模型边界。
- 用户机器字体环境差异。

长期维护时请优先保持这些边界清晰。能跳过不确定修复时，不要为了“自动化率”牺牲图纸正确性；能用真实 CAD 验证时，不要只用构建成功代替运行成功。
