# AFR — CAD 缺失字体自动替换工具

AutoCAD 插件，自动检测并替换图纸中缺失的 SHX / TrueType / 大字体，消除打开 DWG 时的字体丢失警告。

## 插件命名说明

以 `AFR-ACAD2026` 为例：

| 部分 | 含义 |
|---|---|
| `AFR` | 插件代号（Auto Font Replace 的缩写，可忽略） |
| `A` | **A**utodesk |
| `CAD` | Auto**CAD** |
| `2026` | 对应的 AutoCAD **版本号** |

> 💡 请根据自己安装的 CAD 版本下载对应的 DLL 文件。

## 支持的 AutoCAD 版本

| DLL 文件名 | AutoCAD 版本 | .NET 运行时 | 注册表路径 |
|---|---|---|---|
| `AFR-ACAD2026.dll` | AutoCAD **2026**（R25.1） | .NET 8.0 | `R25.1\ACAD-xxxx:xxx` |

> 📌 目前仅支持 AutoCAD 2026。后续版本支持将以独立 DLL 的形式发布，文件名中的版本号会相应变化。

## 功能特性

- **自动检测** — 打开图纸时自动扫描所有文字样式，识别缺失的 SHX、TrueType、大字体
- **一键替换** — 将缺失字体统一替换为用户配置的默认字体
- **多文档支持** — MDI 环境下每个图纸独立处理，互不干扰
- **随 CAD 启动** — 首次 NETLOAD 后自动注册，后续启动 AutoCAD 时自动加载
- **完整卸载** — 提供 `AFRUNLOAD` 命令，一键注销事件并清除注册表项

## 系统要求

| 项目 | 要求 |
|---|---|
| AutoCAD | 见上方 [支持的 AutoCAD 版本](#支持的-autocad-版本) |
| 运行时 | .NET 8.0 |
| 操作系统 | Windows 10 / 11（x64） |

## 安装

1. 编译项目或获取 `AFR-ACAD2026.dll`
2. 启动 AutoCAD 2026
3. 命令行输入 `NETLOAD`，选择 `AFR-ACAD2026.dll`
4. 首次加载后插件会自动注册，后续启动 AutoCAD 将自动加载

## 命令

| 命令 | 说明 |
|---|---|
| `AFR` | 打开字体配置界面，选择替换用的 SHX 主字体和大字体 |
| `AFRUNLOAD` | 完整卸载插件 — 注销事件、删除注册表项、清空运行状态 |

## 使用流程

```
首次使用：
  NETLOAD → 选择 DLL → 输入 AFR → 配置主字体和大字体 → 确认

后续使用（自动）：
  打开 AutoCAD → 自动加载插件 → 打开图纸 → 自动检测并替换缺失字体
```

### 配置界面

输入 `AFR` 命令后弹出字体选择窗口：

- **SHX字体** — 用于替换缺失的 SHX 和 TrueType 字体（必选）
- **大字体** — 用于替换缺失的大字体（可选）
- 下拉列表自动扫描 AutoCAD Fonts 目录下的 `.shx` 文件，支持搜索输入

## 新手教程（零基础手把手）

如果你是第一次使用 AutoCAD 插件，请按以下步骤操作。

### 第一步：获取插件文件

1. 前往 [Releases](https://github.com/splrad/CADFontAutoReplace/releases) 页面
2. 根据你安装的 AutoCAD 版本，下载对应的 DLL 文件（例如 AutoCAD 2026 → `AFR-ACAD2026.dll`）
3. 保存到一个**固定位置**（建议不要放在桌面或临时文件夹）

> 💡 推荐路径：`D:\CADPlugins\AFR-ACAD2026.dll`

### 第二步：加载插件到 AutoCAD

1. 打开 **AutoCAD 2026**
2. 在底部命令行中输入 `NETLOAD`，按回车

   ```
   命令: NETLOAD
   ```

3. 在弹出的文件选择窗口中，找到并选择刚才保存的 `AFR-ACAD2026.dll`，点击"打开"
4. 如果弹出安全警告，选择"加载" / "始终加载"

> ✅ 加载成功后，命令行会显示插件初始化信息。此后每次打开 AutoCAD 都会自动加载，**无需重复操作**。

### 第三步：首次配置替换字体

1. 在命令行输入 `AFR`，按回车

   ```
   命令: AFR
   ```

2. 弹出字体选择窗口：

   - **SHX字体**（必选）— 在下拉框中选择一个字体，例如 `txt.shx` 或 `simplex.shx`。这个字体将用来替换所有缺失的 SHX 和 TrueType 字体
   - **大字体**（可选）— 如果你经常打开含中文的图纸，建议选择 `bigfont.shx` 或 `gbcbig.shx`
   - 下拉框支持直接输入搜索，比如输入 `txt` 就会筛选出 `txt.shx`

3. 选择完成后，点击 **确认** 按钮

> ✅ 配置完成！此后打开任何图纸，插件都会自动替换缺失字体，不需要再做任何操作。

### 第四步：验证效果

打开一张有缺失字体的 DWG 文件，观察命令行底部的输出：

```
[样式: Standard]-SHX字体缺失: old.shx → 替换为: txt.shx
共替换缺失字体数量：1
```

如果看到类似的替换记录，说明插件正在正常工作。

### 日常使用

配置完成后，你**不需要做任何事**。插件的日常工作方式：

```
打开 AutoCAD → 插件自动加载 → 打开图纸 → 自动替换缺失字体 → 正常使用
```

### 如何修改字体配置？

随时在命令行输入 `AFR`，重新选择字体并确认即可。新配置会立即对当前图纸生效。

### 如何卸载插件？

在命令行输入 `AFRUNLOAD`，按回车：

```
命令: AFRUNLOAD
```

插件会自动：
- 停止所有自动替换功能
- 删除注册表中的自动加载配置
- 下次启动 AutoCAD 不再自动加载

> 💡 如果需要重新安装，重启 AutoCAD 后再次 `NETLOAD` 即可。

### 常见问题

<details>
<summary><b>Q: 加载后命令行没有任何显示？</b></summary>

确认你加载的插件是CAD对应版本的DLL，和本项目支持的CAD。
</details>

<details>
<summary><b>Q: 打开图纸后字体仍然缺失？</b></summary>

请检查是否已执行过 `AFR` 命令完成首次配置。插件在首次配置前不会自动替换字体。
</details>

<details>
<summary><b>Q: 想把插件移动到其他文件夹？</b></summary>

插件无法在移动后自动修复路径。请按以下步骤操作：
1. 打开 CAD，输入 `AFRUNLOAD` 卸载插件
2. 关闭 CAD
3. 将 DLL 文件移动到新位置
4. 重新打开 CAD，输入 `NETLOAD` 加载新位置的 DLL

加载后插件会自动将新路径写入注册表，后续启动无需再次 NETLOAD。
</details>

<details>
<summary><b>Q: 替换后文字显示不正确？</b></summary>

尝试选择更通用的替换字体。推荐：
- SHX字体：`txt.shx`（英文图纸）或 `simplex.shx`
- 大字体：`bigfont.shx` 或 `gbcbig.shx`（中文图纸）
</details>

<details>
<summary><b>Q: 如何知道插件是否在工作？</b></summary>

打开图纸后查看命令行底部。如果有缺失字体，会显示替换记录和统计。如果所有字体都正常，会显示"未检测到缺失字体"。
</details>

## 日志输出

插件在命令行输出结构化日志，每个图纸仅显示一次日志头：

```
=============================================
CAD缺失字体自动替换工具 AFR
版本：v2.0-2026/03/21
插件首次加载运行必须执行：AFR
命令说明：
 AFR - 配置替换字体
 AFRUNLOAD - 卸载插件
=============================================
[样式: Standard]-TrueType字体缺失: Arial → 替换为: txt.shx
[样式: Notes]-SHX字体缺失: romans.shx → 替换为: txt.shx
[样式: Chinese]-大字体缺失: chineset.shx → 替换为: bigfont.shx
[信息] 正在处理 'Drawing1.dwg' (触发源: Startup)
替换TrueType字体：1；替换SHX字体：1；替换BigFont字体：1；
共替换缺失字体数量：3
```

## 项目结构

```
AFR-ACAD2026/
├── PluginEntry.cs                  # 插件入口点，事件注册与生命周期管理
├── Commands/
│   └── AfrCommands.cs              # AFR / AFRUNLOAD 命令定义
├── Core/
│   ├── AppInitializer.cs           # 注册表初始化与默认配置创建
│   ├── ExecutionController.cs      # 统一执行控制器（门控、防重复）
│   └── DocumentContextManager.cs   # 文档处理状态跟踪
├── Services/
│   ├── LogService.cs               # 缓冲日志（优先级分桶 + 延迟输出）
│   ├── ConfigService.cs            # 注册表配置（带缓存）
│   ├── RegistryService.cs          # 底层注册表读写
│   ├── FontDetector.cs             # 缺失字体检测（FindFile 缓存）
│   └── FontReplacer.cs             # 字体替换执行
└── UI/
    ├── FontSelectionWindow.xaml     # 字体配置界面（HandyControl）
    ├── FontSelectionWindow.xaml.cs  # 窗口生命周期（无业务逻辑）
    └── FontSelectionViewModel.cs   # 字体选择 ViewModel
```

## 技术架构

### 执行流程

```
AutoCAD 启动
  │
  ├─ PluginEntry.Initialize()
  │   ├─ AppInitializer — 注册表初始化 / 默认配置
  │   ├─ 注册 DocumentCreated 事件
  │   └─ ScheduleExecution(null, "Startup") — 入队等待 Idle
  │
  └─ Application.Idle（AutoCAD 就绪后）
      └─ ExecutionController.Execute()
          ├─ FontDetector.DetectMissingFonts() — 扫描文字样式表
          ├─ FontReplacer.ReplaceMissingFonts() — 执行替换
          ├─ LogService.AddStatistics() — 生成统计
          └─ LogService.Flush() — 一次性输出全部日志
```

### 设计要点

- **延迟执行** — 所有文档处理通过 `Application.Idle` 延迟，确保 AutoCAD 完成加载后再执行
- **缓冲日志** — 日志先写入缓存，按优先级分桶排序后一次性输出，避免与 AutoCAD 消息交错
- **FindFile 缓存** — `ConcurrentDictionary` 缓存字体查找结果，避免重复磁盘 I/O
- **线程安全** — 调度队列、配置缓存、日志缓冲均有锁保护；文档引用使用前检查 `IsDisposed`
- **幂等初始化** — 注册表写入采用 read-then-write 模式，值相同时跳过写入

## 注册表

插件在以下路径存储配置（自动创建）：

```
HKCU\Software\Autodesk\AutoCAD\R25.1\ACAD-xxxx:xxx\Applications\AFR-ACAD2026
  ├─ LOADER        (String)  — DLL 完整路径
  ├─ LOADCTRLS     (DWORD)   — 2（随 AutoCAD 启动加载）
  ├─ MANAGED       (DWORD)   — 1（托管 .NET 插件）
  ├─ DESCRIPTION   (String)  — 插件描述
  ├─ MainFont      (String)  — 配置的主替换字体
  ├─ BigFont       (String)  — 配置的大替换字体
  └─ IsInitialized (DWORD)   — 是否已完成首次配置
```

`AFRUNLOAD` 命令仅删除 `AFR-ACAD2026` 项，不影响其他插件注册表项。

## 构建

```powershell
dotnet build AFR-ACAD2026/AFR-ACAD2026.csproj -c Release
```

依赖项通过 NuGet 自动恢复：
- `AutoCAD.NET` 25.1.0
- `HandyControl` 3.5.1