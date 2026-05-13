# AFR 开发者指南（老手进阶）

> 适合已熟悉项目基本构建和调试流程的开发者。

## 1. 架构约束（必须遵守）

分层方向：

```text
AFR.Core -> AFR.UI -> AFR.AutoCAD -> AFR-ACAD20XX
```

约束：
- `AFR.Core` 与 `AFR.UI` 禁止引用 AutoCAD SDK。
- `AFR.AutoCAD` 不依赖版本壳实现细节。
- 跨层能力通过 `PlatformManager` 定位。

## 2. 命令系统要点

- 命令类位于 `AFR.AutoCAD/Commands`。
- 壳工程 `PluginEntry.cs` 通过 `CommandClass` 声明进行注册。
- `AFRUNLOAD` 是例外：它是隐藏维护入口，不使用 `CommandMethod`/`CommandClass` 注册，避免进入 CAD 自动补全与动态输入建议；插件入口只在 `UnknownCommand` 完整匹配时调用卸载逻辑。
- Debug-only 命令必须双层控制：
  - 命令类文件 `#if DEBUG`
  - `PluginEntry.cs` 注册处 `#if DEBUG`

额外建议：
- 对“只用于排查”的命令统一加 `AFR` 前缀（例如 `AFRVIEW`、`AFRINSERT`），避免命名冲突。
- 命令入口只做编排，复杂逻辑下沉到 `Services` / `FontMapping`。

## 3. Hook 相关开发注意事项

核心文件：`LdFileHook.cs`

风险点：
- 序言长度与跳转补丁（`PrologueSize`）必须与目标版本匹配。
- Native 字符串指针缓存不可随意释放。
- 避免破坏 `_inHook` 递归保护。

建议策略：
1. 先加诊断日志再改逻辑。
2. 单次改动控制在小范围。
3. 每次改动后做实际 DWG 回归验证。

最小回归样本建议：
- 一个“路径残留字体名”DWG；
- 一个“内联 `\F` + `\f` 混合”DWG；
- 一个“缺失字体但未保存损坏”的对照 DWG。

## 4. MText 内联字体链路

关键链路（执行阶段）：

1. `MTextInlineFontReplacer.ReplaceMissingInlineFonts`（同类型替换）
2. `MTextInlineFontScanner.ScanInlineFonts`（重新扫描）
3. `ConvertMissingTrueTypeToShx`（兜底转换）
4. `BuildInlineFixRecords`（日志记录）

注意：若 DWG 文字内容本身编码已损坏，字体替换无法恢复原文。

判定标准（实战）：
- 若 `\F` / `\f` 字体名正常，但正文出现 `Î?²?` 类字符，通常是内容已损坏；
- 若正文正常但字体名缺失，则属于可替换场景。

## 5. 多版本支持扩展

新增版本壳（例如 `AFR-ACAD20XX`）需要：
- `PluginEntry.cs`
- `AutoCad20XXPlatform.cs`（实现 `ICadPlatform`）
- `.csproj` 导入三层 Shared Project
- 命令类注册（含 Debug 命令）

平台常量重点：
- `RegistryBasePath`
- `AcDbDllName`
- `LdFileExport`
- `PrologueSize`

## 6. DBText AI 修复

DBText 单行文字修复不再依赖旧的原生 code page Hook 调查链路，也不再使用公开 JSONL 标签、本地训练或人工标注命令。当前实现使用疑似异常门控、封闭式本地 ONNX AI 模型、确定性候选、特征提取和保守 Decision Engine。

关键文件：

- `src/AutoCAD/AFR.AutoCAD/Services/GlyphCore/TextRepair/GlyphCoreTextRepairService.cs`
- `src/AutoCAD/AFR.AutoCAD/Services/GlyphCore/TextRepair/GlyphCoreTextRepairAdvisor.cs`
- `src/AFR.Core/GlyphCore/TextRepair/`
- `AFR.GlyphCore/tools/`
- `docs/debugging/DBText-Repair-Model.md`

维护原则：

- 未检测到疑似 DBText 异常时不加载文枢模型、不评分、不提示。
- 自动写回必须受疑似异常门控、AI 置信度、分差、可逆转换和安全策略共同约束。
- 普通用户端禁止训练、导入样本、替换模型、修改参数或使用 DBText 纠错 UI。
- 生产 ONNX 模型、训练数据和用户 DWG 属于开发者私有资产，不提交 GitHub；工具、schema、训练脚本和工作台源码位于 `AFR.GlyphCore/tools`。
- 模型或特征 schema 变化时同步 ONNX 嵌入配置、README 和 `docs/debugging/DBText-Repair-Model.md`。
- 不要恢复 `AFRDBTEXTPROBE` / `AFRTRACER*` 等旧探针命令作为当前文档流程。

## 7. 性能与稳定性建议

- 避免在热路径引入高频分配。
- 解析器优先流式处理（`MTextFontParser` 思路）。
- 不做“为了复用而复用”的过度抽象。
- 两段逻辑上下文差异明显时，保留重复实现提升可维护性。

代码层面建议：
- 热路径优先 `Span` / 流式扫描，不做大对象拼接；
- 字体名归一化规则保持唯一来源（后缀、路径、`@` 前缀处理一致）；
- 字典 key 统一 `OrdinalIgnoreCase`，避免大小写回归。

## 8. 调试建议

- 先看 `AFR_Diag_*.log`，再下断点。
- 针对 MText 场景，优先组合命令：
  - `AFRINSERT` 注入测试样本
  - `AFRVIEW` 读取原始内容
  - `AFRLOG` 看修复结果

断点/tracepoint 放置建议：
- `LdFileHook.HookHandler`：看 `fontName`、`normalizedName`、`resolved`；
- `MTextInlineFontReplacer`：看替换前后 `mtext.Contents`；
- `ExecutionController.Execute` 第三阶段：核对三组修复记录合并结果。

## 9. 发布资产生成脚本

发布资产由 `tools/Publish-ReleaseAssets.ps1` 统一生成，不手工复制 DLL 或直接发布 `AFR.Deployer.csproj`。

脚本职责：

1. 自动发现 `src/AutoCAD/AFR-ACAD*/AFR-ACAD*.csproj`；
2. Release 构建每个版本壳；
3. 校验 `artifacts/bin/AFR-ACAD*/release/` 下的插件 DLL 与 `.cad.json` 元数据；
4. 发布 `AFR.Deployer` 自包含单文件 EXE，插件资源由项目文件直接从标准构建输出嵌入；
5. 从 `Version.props` 读取当前版本，将部署器 EXE 复制为 `artifacts/ReleaseAssets/AFR-Deployer_vX.Y.Z.exe`，将 `AFR-ACAD*.dll` 打包为 `artifacts/ReleaseAssets/AFR-DLL_vX.Y.Z.zip`，并复制字体包为 `artifacts/ReleaseAssets/Fonts.zip`。

常用命令：

```powershell
./tools/Publish-ReleaseAssets.ps1
./tools/Publish-ReleaseAssets.ps1 -SkipPluginBuild
```

官方文枢 GlyphCore 模型只在开发者私有环境中注入：

```powershell
./tools/Publish-ReleaseAssets.ps1 `
  -GlyphCoreModelPath C:\PrivateAFR\Models\AFR.GlyphCore.Model.onnx `
  -GlyphCoreModelManifestPath C:\PrivateAFR\Models\AFR.GlyphCore.ModelManifest.json `
  -GlyphCoreExactRepairsPath C:\PrivateAFR\Models\AFR.GlyphCore.ExactRepairs.json `
  -GlyphCoreRuntimeDirectory C:\PrivateAFR\OnnxRuntime\win-x64
```

输出约定：

```text
publish/AFR.Deployer/AFR-Deployer.exe
artifacts/ReleaseAssets/AFR-Deployer_vX.Y.Z.exe
artifacts/ReleaseAssets/AFR-DLL_vX.Y.Z.zip
artifacts/ReleaseAssets/Fonts.zip
```

注意事项：

- EXE 文件名由 `AFR.Deployer.csproj` 的 `<AssemblyName>AFR-Deployer</AssemblyName>` 决定，不在发布后重命名。
- `artifacts/ReleaseAssets/AFR-Deployer_vX.Y.Z.exe` 是 GitHub Release 上传用版本化副本，正式发布输出仍保留在 `publish/AFR.Deployer/`。
- `artifacts/ReleaseAssets/AFR-DLL_vX.Y.Z.zip` 只包含插件主 DLL，不包含 `.cad.json`、`.pdb`、`.xml` 或其它依赖文件。
- `artifacts/ReleaseAssets/Fonts.zip` 来自 `chore/Fonts.zip`，保持固定文件名便于用户识别。
- 新增 CAD 版本时必须确保版本壳 `.csproj` 写入 `CadBrand` / `CadVersion` / `CadRegistryBasePath`，否则 `.cad.json` 元数据不会正确生成。
- `Version.props` 是部署器与插件 DLL 的统一版本来源，发版只修改该文件。

## 10. 回归清单（进阶）

- [ ] 不同触发源（Startup / Command / DocumentCreated）都验证
- [ ] Hook on/off 两条路径验证
- [ ] SHX 主字体 / 大字体 / TrueType 三类都验证
- [ ] MText `\F` / `\f` / 参数段 / 路径残留 / `@` 前缀都覆盖
- [ ] DBText AI 模型缺失、schema 不匹配、低置信度和高置信写回路径已验证
- [ ] Release 构建下 Debug 功能完全排除

## 11. 代码评审关注点（建议）

- 是否破坏层级依赖方向；
- 是否引入无必要抽象；
- 是否改动了 Hook 热路径且缺少日志/回归；
- 文档与命令入口是否同步更新（`README` / `developer-guide` / `PluginEntry`）。

---

入口导航：[`developer-guide-beginner.md`](developer-guide-beginner.md)
