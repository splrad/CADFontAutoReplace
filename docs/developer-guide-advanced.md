# AFR 开发者指南（老手进阶）

> 适合已熟悉项目基本构建和调试流程的开发者。

## 1. 架构约束

分层方向：

```text
AFR.Core -> AFR.UI -> AFR.AutoCAD -> AFR-ACAD20XX
```

约束：

- `AFR.Core` 与 `AFR.UI` 禁止引用 AutoCAD SDK。
- `AFR.AutoCAD` 不依赖版本壳实现细节。
- 跨层能力通过 `PlatformManager` 定位。
- 项目不再包含任何单行文字编码修复、AI 推理、训练、候选包、报告或补绘链路。

## 2. 命令系统要点

- 命令类位于 `AFR.AutoCAD/Commands`。
- 壳工程 `PluginEntry.cs` 通过 `CommandClass` 声明进行注册。
- `AFRUNLOAD` 是隐藏维护入口，不使用 `CommandMethod`/`CommandClass` 注册，避免进入 CAD 自动补全与动态输入建议。
- Debug-only 命令必须双层控制：命令类文件 `#if DEBUG`，`PluginEntry.cs` 注册处 `#if DEBUG`。

## 3. Hook 相关开发注意事项

保留的 native Hook 边界：

- `NativeInlineHook`：底层 inline patch 基础设施。
- `NativeHookTarget`、`NativeFontHookProfile`、`INativeFontHookExportsProvider`：字体 Hook profile 与平台导出/RVA 信息。
- `FontRuntimeRequestRegistry`：来源 Hook 与文件级 Hook 之间的统一登记表。
- `LdFileHook`：唯一 SHX 文件级映射执行点，未登记请求立即放行。
- `ShpLoadHook`：唯一 TrueType 文件级映射执行点，未登记请求立即放行。
- `StyleTextStyleHook`：只识别样式表来源的缺失 `@TrueType` 并登记到 `ShpLoadHook`。
- `MTextInlineFontHook`：只识别 MText 内联字体来源并按字体类型登记到 `LdFileHook` 或 `ShpLoadHook`。

`LdFileHook`、`ShpLoadHook`、`StyleTextStyleHook` 和 `MTextInlineFontHook` 均随插件持久安装；`ExecutionController.Execute()` 只管理文档级运行时状态。文档执行顺序必须保持为：清理运行时登记和候选 → 原始样式表检测 → 样式表 `@TrueType` 运行时映射 → MText 内联运行时映射 → 样式表最终写回 → 替换后二次检测 → 清理运行时登记和候选 → 最终图形刷新。

普通样式表检测和永久写回应优先使用 AutoCAD 托管 API，例如当前 `Database` 上的 `HostApplicationServices.Current.FindFile`；`FontAvailabilityIndex` 只是 native Hook 路径无法安全取得托管 `Database` 时的 SHX 兜底索引。TrueType face 可用性由 `GdiTrueTypeFontFaceIndex` 通过 `EnumFontFamiliesExW` 按字体名精确查询，不能用基础字体存在推断 `@TrueType` 存在，也不能在 CAD 启动同步热路径中预热全量 GDI face 索引。

样式表 `@SHX` 主字体和大字体缺失走永久替换；样式表 `@TrueType` 保留运行时映射，不要永久写回样式表。

所有文件级映射都必须先写入 `FontRuntimeRequestRegistry` 再进入可能触发 `ldfile` / `shpload` 的原生 trampoline、`LoadStyleRec` 或 `Regen`。`LdFileHook` / `ShpLoadHook` 不允许扫描全局字体、不允许推导来源，也不允许处理未登记请求。

风险点：

- 序言长度与跳转补丁必须与目标版本匹配。
- Native 字符串指针缓存不可随意释放。
- 避免破坏 `_inHook` 递归保护。

## 4. MText 内联字体链路

关键链路（执行阶段）：

1. `MTextInlineFontHook` 随插件持久安装，但没有内联候选时必须快速放行。
2. `MTextInlineFontScanner.ScanInlineFonts` 在文档处理周期内只读扫描 `\F` / `\f`，并把候选写入 `MTextInlineFontHook.ReplaceInlineFontCandidates()`。
3. `MTextInlineFontHook.PreRegisterRuntimeRequests()` 按候选类型登记文件级运行时请求；未登记时不触发 MText Regen。
4. `Editor.Regen()` 只在有文件级登记时触发 CAD MText 展开/绘制流程，让 Hook 命中实际内联字体。
5. `FontRuntimeMappingStore.GetRuntimeMappingResults` 读取 `LdFileHook` / `ShpLoadHook` 实际命中的文件级映射。

注意：若 DWG 文字内容本身编码已损坏，字体替换无法恢复原文。不要恢复改写 `MText.Contents` 的旧转换器，也不要恢复 setter 直接替换；当前策略是扫描内容、登记缺失字体、触发 CAD 原生展开流程、由文件级 Hook 记录真实运行时映射。

## 5. 多版本支持扩展

新增版本壳（例如 `AFR-ACAD20XX`）需要：

- `PluginEntry.cs`
- `AutoCad20XXPlatform.cs`（实现 `ICadPlatform` 与 `INativeFontHookExportsProvider`）
- `.csproj` 导入三层 Shared Project
- 命令类注册（含 Debug 命令）

平台常量重点：

- `RegistryBasePath`
- `AcDbDllName`
- `LdFileExport`
- 字体 Hook 所需导出名/RVA/prefix

## 6. 发布资产生成脚本

发布资产由 `tools/Publish-ReleaseAssets.ps1` 统一生成，不手工复制 DLL 或直接发布 `AFR.Deployer.csproj`。

常用命令：

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

注意事项：

- EXE 文件名由 `AFR.Deployer.csproj` 的 `<AssemblyName>AFR-Deployer</AssemblyName>` 决定，不在发布后重命名。
- `artifacts/ReleaseAssets/AFR-DLL_vX.Y.Z.zip` 只包含插件主 DLL，不包含 `.cad.json`、`.pdb`、`.xml` 或其它依赖文件。
- `artifacts/ReleaseAssets/Fonts.zip` 来自 `chore/Fonts.zip`。
- 新增 CAD 版本时必须确保版本壳 `.csproj` 写入 `CadBrand` / `CadVersion` / `CadRegistryBasePath`。
- `Version.props` 是部署器与插件 DLL 的统一版本来源，发版只修改该文件。

## 7. 回归清单

- [ ] 不同触发源（Startup / Command / DocumentCreated）都验证
- [ ] Hook on/off 两条路径验证
- [ ] SHX 主字体 / 大字体 / TrueType 三类都验证
- [ ] 样式表 `@TrueType` 运行时映射和 `@SHX` 永久替换都验证
- [ ] MText `\F` / `\f` / 参数段 / 路径残留 / `@` 前缀都覆盖
- [ ] 所有进入 `LdFileHook` 的字体都在原生加载前完成预登记
- [ ] Release 构建下 Debug 功能完全排除

## 8. 代码评审关注点

- 是否破坏层级依赖方向；
- 是否引入无必要抽象；
- 是否改动了 Hook 热路径且缺少日志/回归；
- 文档与命令入口是否同步更新（`README` / `developer-guide` / `PluginEntry`）。

---

入口导航：[`developer-guide-beginner.md`](developer-guide-beginner.md)
