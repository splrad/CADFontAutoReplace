# AFR 开发者指南（纯新手）

> 适合第一次参与本项目开发的同学。

## 1. 你先知道这 3 件事

1. 这个项目最终只产出一个 AutoCAD 插件 DLL（按版本命名，如 `AFR-ACAD20XX.dll`）。
2. 常用开发入口工程是对应版本壳目录：`src/AutoCAD/AFR-ACAD20XX/`。
3. 新增命令后如果不在 `PluginEntry.cs` 注册，CAD 会提示“无此命令”。

## 2. 项目结构（最简版）

```text
AFR.Core（基础接口/模型）
AFR.UI（WPF 窗口）
AFR.AutoCAD（命令 + 业务逻辑）
AFR-ACAD20XX（最终输出 DLL 的版本壳工程）
```

一句话理解：
- `AFR-ACAD20XX` 负责“打包和注册”；
- 真正业务代码主要在 `AFR.AutoCAD`。

## 3. 本地环境准备

- Visual Studio 2022 或更高版本
- .NET 8 SDK
- 对应版本的 AutoCAD（与目标版本壳匹配）

建议一直用 Debug 配置开发。

## 4. 第一次编译

在仓库根目录运行：

```bash
dotnet build src/AutoCAD/AFR-ACAD20XX/AFR-ACAD20XX.csproj
```

`20XX` 请替换为当前目标版本（例如 `2026`）。

看到“生成成功”即可。

## 5. 第一次调试

1. 在 VS 中启动当前目标版本壳（如 `AFR-ACAD20XX`）调试。
2. AutoCAD 打开后，命令行输入：
   - `AFR`（配置替换字体）
   - `AFRLOG`（查看替换日志）
   - `AFRUNLOAD`（卸载插件）
   - `AFRVIEW`（Debug 下查看 MText 内容）
   - `AFRINSERT`（Debug 下插入测试 MText）

## 6. 10 分钟上手路径（推荐）

按顺序做这 5 步：

1. `dotnet build` 确认本地能编译。
2. 启动目标版本壳调试，确保 CAD 正常拉起。
3. 在 CAD 里执行 `AFR`，完成一次字体配置。
4. 执行 `AFRINSERT` 插入测试样本，再执行 `AFRLOG` 看结果。
5. 执行 `AFRVIEW` 检查 MText 原始内容，建立“输入-输出”直觉。

## 7. 新增命令的正确姿势（最容易漏）

### 6.1 写命令类

放到：`src/AutoCAD/AFR.AutoCAD/Commands/`

并写：

```csharp
[CommandMethod("YOURCMD")]
public void YourCommand() { }
```

### 6.2 注册命令类（必须）

修改：`src/AutoCAD/AFR-ACAD20XX/PluginEntry.cs`

```csharp
[assembly: CommandClass(typeof(AFR.Commands.YourCommandClass))]
```

如果是 Debug 专用命令，放在 `#if DEBUG` 块内。

建议模板：

```csharp
#if DEBUG
[assembly: CommandClass(typeof(AFR.Commands.YourDebugCommand))]
#endif
```

## 8. 新手常见错误

### 错误 1：命令写了但 CAD 说无此命令

原因：没在 `PluginEntry.cs` 注册 `CommandClass`。

### 错误 2：改了 UI 层却引用 AutoCAD 类型

`AFR.UI` / `AFR.Core` 不允许依赖 AutoCAD SDK，AutoCAD 类型要放在 `AFR.AutoCAD`。

### 错误 3：改 Hook 后行为异常

`LdFileHook` 涉及非托管逻辑，改动要小、每次改完都实测。

### 错误 4：改了代码但 CAD 行为没变化

常见原因：
- 仍在运行旧 DLL；
- 未重启调试会话；
- Debug/Release 条件编译分支不一致。

排查顺序：
1. 看 VS 输出中实际加载 DLL 路径；
2. 确认当前是 Debug 配置；
3. 重新构建并重启调试。

## 9. 日常开发建议（新手版）

- 先定位，再改代码：优先看 `ExecutionController` 调用链。
- 先加日志，再动逻辑：避免“改完不知道哪里坏”。
- 每次只改一个小目标：例如“只改 MText 解析，不碰 Hook”。

## 10. 提交前快速检查

- [ ] `dotnet build` 通过
- [ ] 新命令已注册到 `PluginEntry.cs`
- [ ] 没有把 AutoCAD 类型放进 `AFR.Core` / `AFR.UI`
- [ ] Debug 功能已用 `#if DEBUG` 控制
- [ ] 新文档/命令在 README 或入口文档可被找到

---

下一步建议阅读：[`developer-guide-advanced.md`](developer-guide-advanced.md)
