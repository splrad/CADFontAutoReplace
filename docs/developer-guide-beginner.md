# AFR 开发者指南（纯新手）

> 适合第一次参与本项目开发的同学。

## 1. 你先知道这 3 件事

1. 这个项目最终产出 AutoCAD 插件 DLL（按版本命名，如 `AFR-ACAD20XX.dll`）。
2. 常用开发入口工程是对应版本壳目录：`src/AutoCAD/AFR-ACAD20XX/`。
3. 新增命令后如果不在 `PluginEntry.cs` 注册，CAD 会提示“无此命令”。

## 2. 项目结构（最简版）

```text
AFR.Core（基础接口/模型）
AFR.UI（WPF 窗口）
AFR.AutoCAD（命令 + 字体替换业务逻辑）
AFR-ACAD20XX（最终输出 DLL 的版本壳工程）
```

一句话理解：

- `AFR-ACAD20XX` 负责“打包和注册”；
- 真正业务代码主要在 `AFR.AutoCAD`。

本项目不再包含任何单行文字编码修复、AI 模型、训练、候选包、报告或补绘链路。

## 3. 本地环境准备

- Visual Studio 2026 或更高版本
- .NET SDK（仓库同时覆盖 `net462`、`net472`、`net48`、`net8.0-windows`、`net10.0-windows`）
- 对应版本的 AutoCAD（与目标版本壳匹配）

建议一直用 Debug 配置开发，并直接打开仓库根目录的 `CADFontAutoReplace.slnx`。

## 4. 第一次编译

在仓库根目录运行：

```bash
dotnet build src/AutoCAD/AFR-ACAD20XX/AFR-ACAD20XX.csproj
```

`20XX` 请替换为当前目标版本（例如 `2026`）。

## 5. 第一次调试

1. 在 VS 中启动当前目标版本壳（如 `AFR-ACAD20XX`）调试。
2. AutoCAD 打开后，命令行输入：
   - `AFR`（配置替换字体）
   - `AFRLOG`（查看替换日志）
   - `AFRVIEW`（仅 Debug：查看 MText 内容）

`AFRUNLOAD` 是隐藏维护入口：Debug/Release 均可完整输入执行，但不会出现在 CAD 自动补全、动态输入建议或命令横幅中。

## 6. 10 分钟上手路径（推荐）

1. `dotnet build` 确认本地能编译。
2. 启动目标版本壳调试，确保 CAD 正常拉起。
3. 在 CAD 里执行 `AFR`，完成一次字体配置。
4. 执行 `AFRLOG` 查看当前图纸的检测与替换结果。
5. 如需调试 MText 场景，在 Debug 构建下执行 `AFRVIEW` 建立“输入-输出”直觉。

## 7. 生成发布资产

正式分发默认使用部署器安装，不手动 `NETLOAD`。生成 GitHub Release 发布资产时，在仓库根目录运行：

```powershell
./tools/Publish-ReleaseAssets.ps1
```

脚本会自动：

1. Release 构建所有 `src/AutoCAD/AFR-ACAD20XX/` 插件 DLL；
2. 校验 `artifacts/bin/AFR-ACAD20XX/release/` 下的插件 DLL 与 `.cad.json` 元数据；
3. 从标准构建输出直接嵌入插件资源并发布自包含单文件部署器；
4. 将版本化 EXE、纯 DLL 压缩包与字体包生成到 `artifacts/ReleaseAssets/`。

如果只是调试部署器界面或资源嵌入，且插件 DLL 已经构建过，可跳过插件重建：

```powershell
./tools/Publish-ReleaseAssets.ps1 -SkipPluginBuild
```

## 8. 新增命令的正确姿势

命令类放到：`src/AutoCAD/AFR.AutoCAD/Commands/`

```csharp
[CommandMethod("YOURCMD")]
public void YourCommand() { }
```

然后修改目标版本壳 `PluginEntry.cs`：

```csharp
[assembly: CommandClass(typeof(AFR.Commands.YourCommandClass))]
```

如果是 Debug 专用命令，命令文件和注册处都放在 `#if DEBUG` 块内。

## 9. 新手常见错误

### 错误 1：命令写了但 CAD 说无此命令

原因：没在 `PluginEntry.cs` 注册 `CommandClass`。

### 错误 2：改了 UI 层却引用 AutoCAD 类型

`AFR.UI` / `AFR.Core` 不允许依赖 AutoCAD SDK，AutoCAD 类型要放在 `AFR.AutoCAD`。

### 错误 3：改 Hook 后行为异常

`LdFileHook`、`StyleTextStyleHook`、`MTextInlineFontHook` 涉及非托管逻辑，改动要小、每次改完都实测。数据库读写、样式表检测、字体文件查找应优先使用 AutoCAD 托管 API；字体加载期重定向、样式表 `@TrueType` 运行时映射和 MText 内联字体运行时映射才使用 Hook。

样式表 `@SHX` 主字体和大字体缺失走永久替换；样式表 `@TrueType` 保留运行时映射，不要永久写回样式表。

### 错误 4：改了代码但 CAD 行为没变化

常见原因：

- 仍在运行旧 DLL；
- 未重启调试会话；
- Debug/Release 条件编译分支不一致。

## 10. 提交前快速检查

- [ ] `dotnet build` 通过
- [ ] 发布前已运行 `Publish-ReleaseAssets.ps1` 生成发布资产
- [ ] 新命令已注册到 `PluginEntry.cs`
- [ ] 没有把 AutoCAD 类型放进 `AFR.Core` / `AFR.UI`
- [ ] Debug 功能已用 `#if DEBUG` 控制
- [ ] 新文档/命令在 README 或入口文档可被找到

---

下一步建议阅读：[`developer-guide-advanced.md`](developer-guide-advanced.md)
