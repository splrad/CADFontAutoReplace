# CADFontAutoReplace 项目交接说明

## 当前范围

CADFontAutoReplace 当前只维护 AutoCAD 缺失字体自动替换和字体运行时映射能力：

- 样式表缺失字体检测与永久替换。
- 样式表 `@` 字体运行时映射。
- `LdFileHook` 字体加载重定向。
- `MTextInlineFontHook` 内联字体运行时映射。
- `AFR` 字体配置、`AFRLOG` 替换日志、`AFRUNLOAD` 隐藏卸载入口。
- `AFR.Deployer` 一键安装、卸载和发布资产生成。

项目不再处理任何单行文字编码修复、AI 决策、native decode evidence、训练工具、候选包、模型、报告或补绘问题。

## 代码结构

```text
src/AFR.Core              基础接口、模型、配置和共享服务
src/AFR.UI                WPF 窗口与 ViewModel
src/AFR.AutoCAD           AutoCAD 命令、字体检测替换、Hook 和执行编排
src/AutoCAD/AFR-ACAD20XX  各 AutoCAD 版本壳工程
src/AFR.Deployer          部署器
tools                     发布脚本
docs                      使用与开发文档
```

依赖方向保持：

```text
AFR.Core -> AFR.UI -> AFR.AutoCAD -> AFR-ACAD20XX
```

`AFR.Core` 与 `AFR.UI` 不引用 AutoCAD SDK；AutoCAD 相关类型留在 `AFR.AutoCAD` 和版本壳中。

## 执行流程

`ExecutionController.Execute` 是字体处理主流程：

1. 检测样式表缺失字体。
2. 永久替换普通缺失字体。
3. 二次检测并记录仍缺失样式。
4. 登记并触发样式表 `@` 字体运行时映射。
5. 扫描 MText 内联字体，临时安装 MText Hook，通过 Regen 收集实际映射结果。
6. 需要时执行最终 Regen，输出统计并写入 `AFRLOG` 可读取的上下文。

该流程不包含任何单行文字修复阶段。

## Hook 边界

必须保留的共享字体 Hook 基础设施：

- `NativeInlineHook`
- `NativeHookTarget`
- `NativeFontHookProfile`
- `INativeFontHookExportsProvider`

必须保留的字体 Hook：

- `LdFileHook`
- `StyleTextStyleHook`
- `MTextInlineFontHook`

这些 Hook 只服务字体加载、样式表运行时映射和 MText 内联字体映射，不作为任何文字编码修复信号。

## 发布

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

发布脚本不再接受模型、模型清单或原生推理运行时参数。

## 维护规则

- 新增命令先登记到 `CommandNames.cs`，再在目标版本壳 `PluginEntry.cs` 注册。
- Debug-only 命令必须同时在命令文件和注册处用 `#if DEBUG` 控制。
- 修改 Hook 热路径时保持小范围变更，并用真实 CAD 图纸验证。
- 样式表普通缺失字体走永久替换；样式表 `@` 字体只做运行时映射。
- MText 内联字体不改写 `MText.Contents`，映射记录以 Hook 实际命中为准。
- 文档入口为 `README.md`、`docs/developer-guide-beginner.md`、`docs/developer-guide-advanced.md` 和 `.github/copilot-instructions.md`。

## 验证清单

- `dotnet build CADFontAutoReplace.slnx -c Debug`
- `dotnet build CADFontAutoReplace.slnx -c Release`
- `AFR` 能完成字体配置和当前图纸处理。
- `AFRLOG` 能展示样式表检测、替换状态、样式表运行时映射和 MText 内联映射。
- `LdFileHook`、`StyleTextStyleHook`、`MTextInlineFontHook` 在各自边界内工作。
- 发布脚本能生成部署器、DLL 包和字体包。
