# Copilot Instructions

### Think Before Coding（编码前先思考）

**不假设、不隐藏困惑、暴露权衡。**

实现之前：
- 显式声明假设。不确定时先问。
- 存在多种解读时，列出来 — 不要默默选一个。
- 如果有更简单的方案，直说。该反对时就反对。
- 不清楚就停下。说出哪里困惑，然后问。

### Simplicity First（简单优先）

**用最少代码解决问题。不做投机性设计。**

- 不为单次使用的代码引入抽象层。
- 不添加未被请求的"灵活性"或"可配置性"。
- 不为不可能发生的场景写错误处理。
- 如果 200 行能压到 50 行，重写它。

自问："资深工程师会觉得这过度复杂吗？"如果是，简化。

### Goal-Driven Execution（目标驱动执行）

**定义成功标准。循环直到验证通过。**

把任务转化为可验证的目标：
- "加校验" → "为非法输入写测试，然后让它们通过"
- "修 bug" → "写一个能复现它的测试，然后让它通过"
- "重构 X" → "确保重构前后测试都通过"

多步任务先列简短计划：
1. [步骤] → 验证：[检查项]
2. [步骤] → 验证：[检查项]
3. [步骤] → 验证：[检查项]
强成功标准 → 可独立循环；弱标准（"让它工作"）→ 需要不断澄清。

**生效信号**：diff 中无关改动变少、因过度复杂导致的重写变少、澄清问题出现在实现前而非犯错后。

---

## 项目概述

采用 Shared Project 架构实现单 DLL 分发。项目插件版本号使用“主版本.次版本”的 0.0 格式，日志头应与统一插件版本号同步。文档应默认采用基于部署器的安装方式，不应在主 README 中描述手动 NETLOAD 插件加载。支持的 CAD 版本应在主 README 中的表格中列出，并按从低到高的顺序排序。

## 架构规则

### 分层结构（严格遵守依赖方向）
AFR.Core（纯 .NET，零 CAD 依赖）
    ↑
AFR.UI（WPF + HandyControl，零 CAD 依赖）
    ↑
AFR.AutoCAD（AutoCAD 通用逻辑，引用 AutoCAD SDK）
    ↑
AFR-ACAD20XX（版本适配壳，仅 PluginEntry + ICadPlatform 实现）
- `AFR.Core` 和 `AFR.UI` **禁止**引用任何 AutoCAD SDK 类型
- `AFR.AutoCAD` 不得引用具体版本适配壳的类型
- 跨层通信通过 `PlatformManager` 静态服务定位器，不使用 DI 容器

### 命名空间

所有项目共用根命名空间 `AFR`，子命名空间按职责划分：
- `AFR.Abstractions` — 接口定义
- `AFR.Models` — 数据模型（record / record class）
- `AFR.Platform` — PlatformManager
- `AFR.Services` — 服务实现（Core 层和 AutoCAD 层共用此命名空间）
- `AFR.Hosting` — 插件生命周期
- `AFR.FontMapping` — Hook 与 MText 解析
- `AFR.Commands` — AutoCAD 命令
- `AFR.UI` — ViewModel 与 WPF 窗口

### Shared Project 约束

四个 Shared Project（AFR.Core / AFR.UI / AFR.AutoCAD / AFR.HostIntegration）的源码在编译时嵌入最终 DLL。新增文件时：
- 放入对应 Shared Project 目录，自动被 `.projitems` 包含
- XAML 文件需确认 `.projitems` 中的 `Generator` 和 `SubType` 设置正确

## 编码规范

### C# 风格

- 目标框架按项目而定（当前仓库覆盖 `net462`、`net472`、`net48`、`net8.0-windows`、`net10.0-windows`），启用 `nullable` 与项目既有约定；新代码需遵循所在项目当前配置
- 数据模型优先使用 `record` 或 `sealed record`（如 `FontCheckResult`）
- 服务类使用 `internal sealed class`（非接口实现用 `static class`）
- 接口放在 `AFR.Core/Abstractions/`，命名以 `I` 开头
- 单例模式使用 `Lazy<T>` 惰性初始化（参考 `ExecutionController`、`LogService`）
- XML 文档注释覆盖所有 `public` / `internal` 类型及其公共成员，使用 `<summary>` + `<para>` 格式
- 条件编译：`DiagnosticLogger` 仅在 `#if DEBUG` 下编译，Release 自动移除

### WPF / MVVM

- ViewModel 实现 `INotifyPropertyChanged`，不使用 MVVM 框架
- ViewModel 不直接操作注册表或 AutoCAD API，由命令层桥接
- 窗口定位使用 `WindowPositionHelper` 居中于 AutoCAD 主窗口
- 插件侧 UI 控件库为 HandyControl（已嵌入为程序集资源）；部署工具使用 WPF-UI

### 重构策略

- **保守策略**：当两个方法的上下文、数据源、日志、校验时机存在差异时，即使有 3 行级别的代码形式重复，也不应强行提取为共享方法。优先保持每个方法的独立可读性，避免为减少少量重复而引入复杂的参数列表或闭包
- 不要为了"面向未来"添加当前不需要的抽象层
- 修改 Hook 相关代码（`LdFileHook`、`AutoCadFontHook`）时格外谨慎，涉及非托管内存和函数指针

## 添加新 CAD 版本支持

在 `src/AutoCAD/` 下创建新目录（如 `AFR-ACAD2025/`），包含：
1. `PluginEntry.cs` — 继承 `PluginEntryBase`，实现 `CreatePlatform()` / `CreateFontHook()` / `CreateHost()`。注意必须添加 `[assembly: ExtensionApplication(typeof(AFR.PluginEntry))]` 及命令类的特性标签（包括 DEBUG 下的 MTextEditorCommand），且 Hook 和 Host 可直接返回共享层的 `new AutoCadFontHook()` 和 `new AutoCadHost()`。
2. `AutoCad20XXPlatform.cs` — 实现 `ICadPlatform`，填入版本特定常量。
3. `.csproj` — 导入所需 Shared Project 的 `.projitems`（至少 `AFR.Core` / `AFR.UI` / `AFR.AutoCAD`，按版本需要补充 `AFR.Polyfills` 与 `AFR.HostIntegration`）。当前仓库已关闭中心化包管理（`Directory.Packages.props` 中 `ManagePackageVersionsCentrally=false`），AutoCAD.NET 版本在各版本壳项目中通过 `PackageReference Version="xx.x.x"` 单独声明，并保留 `ExcludeAssets="runtime"` 配置。

## 关键设计约束

- **统一执行控制**：`ExecutionController` 统一协调执行流程，处理 Startup/Command/DocumentCreated 事件，内部实行同文档单次执行防重复保护和 `IsInitialized` 门控（未配置时跳过）。
- TrueType 替换策略分两个层面：
  - **样式表级别**（ldfile Hook + FontReplacer）：TrueType 必须用 TrueType 替换，不可用 SHX（否则污染 AutoCAD 内部字体缓存，导致乱码 + ST 弹窗）
  - **MText 内联字体**（MTextInlineFontReplacer）：缺失的 TrueType `\f` 格式代码转换为 SHX `\F` 格式代码（指向用户配置的 SHX 主字体+大字体），使渲染走 ldfile 路径由 Hook 统一管理
- SHX 主字体（param2=0）和大字体（param2=4）均通过 ldfile Hook 统一重定向；形文件（param2=2）跳过
- `Marshal.StringToHGlobalUni` 分配的原生字符串指针**永不释放**（AutoCAD 可能缓存指针）
- `FontDetectionContext` 按事务隔离，不同图纸/执行次数之间零共享
- ShapeFile 样式（`ltypeshp.shx` 等）始终跳过，不参与替换
- 文档注释使用中文

## 验证 CAD DLL Hook 函数的标准步骤

1. **确认导出符号**（dumpbin，取 RVA）：dumpbin /exports '<dll路径>' | Select-String 'ldfile'   输出格式：`序号 ordinal RVA 修饰名`

2. **读取函数入口字节**（PE 解析 RVA → 文件偏移，读 32 字节）：$dllPath='<dll路径>'; $rva=0x<RVA>; $bytes=[System.IO.File]::ReadAllBytes($dllPath); $peOffset=[BitConverter]::ToInt32($bytes,0x3C); $numSections=[BitConverter]::ToUInt16($bytes,$peOffset+6); $optHeaderSize=[BitConverter]::ToUInt16($bytes,$peOffset+20); $sectionBase=$peOffset+24+$optHeaderSize; $fileOffset=$null; for($i=0;$i-lt$numSections;$i++){$off=$sectionBase+$i*40;$vAddr=[BitConverter]::ToUInt32($bytes,$off+12);$vSize=[BitConverter]::ToUInt32($bytes,$off+16);$rawOff=[BitConverter]::ToUInt32($bytes,$off+20);if($rva-ge$vAddr-and$rva-lt($vAddr+$vSize)){$fileOffset=$rawOff+($rva-$vAddr);break}}; if($null-eq$fileOffset){"RVA not found";exit}; $dump=$bytes[$fileOffset..($fileOffset+31)]|ForEach-Object{$_.ToString('X2')}; "FileOffset=0x{0:X8}" -f $fileOffset; "Bytes: "+($dump-join' ')
3. **逐指令解析验证 PrologueSize**：
   - 逐字节识别 x64 指令边界
   - 确认 `PrologueSize`（通常 21）恰好落在完整指令末尾
   - 确认被复制字节中**不含 RIP 相对寻址**（`lea/mov [rip+...]`），若有则需 Trampoline 重定位

4. **判断通过条件**：
   - ✅ 导出名与平台常量 `LdFileExport` 一致
   - ✅ `PrologueSize` 边界对齐完整指令
   - ✅ 序言中无 RIP 相对寻址，Trampoline 可直接复制

## 第三方库文档

所有第三方库应在一个统一的部分中进行文档记录，而不是分成不同的类别。