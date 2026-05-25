# 字体 Hook 证据与边界

本文是 AFR 字体 Hook 的证据档案和验收边界入口。它同时记录两类事实：

- 当前默认保留：当前源码仍安装、编译、执行和验收的 Hook。
- 历史已证明事实：旧链路中已经通过源码、日志或真实 CAD 运行证明过的 Hook 行为，即使这些 Hook 后来已经删除，也应保留结论和边界。

当前默认维护范围仍然只有文件级运行时映射：`LdFileHook` 处理 SHX 主字体/大字体加载，`ShpLoadHook` 只处理 confirmed TrueType / `@TrueType` 加载。来源级 Hook、MText 候选扫描、setter 直接替换和 `MText.Contents` 改写都不是当前默认修复路径。

阅读本文时先记住一句话：Hook 安装成功不等于字体映射成功。只有真实 `HookHandler` 命中、native 参数被替换、redirect 计数增加，并且 `FontRuntimeMappingStore` 写入结果，才可以把一次文件级字体映射视为成功。

## 状态定义

| 状态 | 含义 |
| --- | --- |
| 当前默认保留 | 当前源码仍保留在安装、编译和执行链路中，可以作为默认维护目标。 |
| 历史已证明但已移除 | 曾经通过真实日志或代码验证证明有效，但当前源码已删除或不再默认安装。 |
| 已证伪/已排除 | 曾被怀疑可用，但真实日志证明不是有效边界，或命中为 0。 |
| 实验未证明映射成功 | 只证明了可安装、可登记或可构建，尚未证明真实 DWG 中发生 redirect。 |
| 历史辅助链路 | 曾用于登记、对账或诊断，但它本身不是映射成功证据。 |

## 证据分级

| 证据类型 | 能证明什么 | 不能证明什么 |
| --- | --- | --- |
| 源码证据 | 当前维护的入口、执行顺序、fail-closed 条件和禁止分支。 | 不能证明某个 DWG 在某台 CAD 上已经命中过 Hook。 |
| 启动/安装日志 | Hook 是否完成导出解析、入口校验和 inline hook 安装。 | 不能证明后续字体请求一定进入 `HookHandler`。 |
| 入站命中日志 | `HookHandler` 已被 AutoCAD 调用，能看到 native 参数样本。 | 不能证明请求一定被替换；可能被严格放行。 |
| redirect 日志和计数 | native 参数已替换，`redirects` 增加。 | 不能证明样式表永久字段已改好；样式表写回由 `FontReplacer` 负责。 |
| `FontRuntimeMappingStore` 结果 | AFRLOG / AFRVIEW 可以展示的运行时映射结果。 | 不能覆盖没有触发文件加载的历史文本或已缓存图形。 |
| 历史调查日志 | 说明某个入口曾经在特定版本、DWG、部署 DLL 中命中。 | 不能直接当作当前 checkout、当前 CAD、当前 DWG 的成功证据。 |

无效证据包括：只看到 Hook 注册成功、只看到候选扫描命中、只看到入口 RVA 相近、只看到样式表检测缺失、只看到 UI 有旧缓存结果。这些信息可以辅助定位，但不能单独证明运行时映射成功。

## 已证明 Hook 事实总表

| Hook / 入口 | 状态 | 已证明事实 | 当前结论 |
| --- | --- | --- | --- |
| `LdFileHook` / `ldfile` | 当前默认保留 | SHX 主字体/大字体文件级加载可真实进入 `HookHandler` 并 redirect；`@gbcbig.shx` 调查中出现 `HookHandler request='gbcbig.shx'`、`request='@gbcbig.shx'`、`Redirects=946`。 | 当前 SHX 文件级运行时映射入口。 |
| `ShpLoadHook` / `shpload` | 当前默认保留 | TrueType / `@TrueType` 可在 `shpload` 阶段采样并重定向；真实参数可能出现在 `arg6`、`fileName`、`arg5`。 | 当前 TrueType 文件级运行时映射入口，只接受 confirmed TrueType。 |
| `StyleTextStyleHook` / `AcGiTextStyle::loadStyleRec` | 历史已证明但已移除 | 样式表运行时映射曾由 `loadStyleRec` 承载；主动 `TextStyle.LoadStyleRec` 后可让样式表 SHX / TrueType 缺失进入运行时对账。 | 不属于当前默认链路；历史结论只能用于比较或重新设计。 |
| `MTextInlineFontHook` / `setFont` / `setFileName` / `setBigFontFileName` | 历史已证明但已移除 | MText 内联字体曾通过 setter 路径发生运行时映射；旧 direct setter replacement 能在活动 `Regen()` 周期中改变 native 参数并影响显示缓存。 | 不属于当前默认链路；不要为了恢复映射数量而恢复默认 setter 替换。 |
| `AcDbMText::explodeFragments` | 已证伪/已排除 | 真实日志中 `ExplodeFragmentsHits=0`，但 `SetFontHits`、`SetFileNameHits`、`SetBigFontFileNameHits` 和 `shpload TrueType 重定向` 有命中。 | 不是该 MText 字体加载流程的有效边界。 |
| `MapFontDiagnosticHook` / `mapFont` | 实验未证明映射成功 | 可作为早期登记/诊断实验点，`MapFontEarlyRegister` 不替换 trampoline 参数。 | 不能把 mapFont 登记当作映射成功；当前不恢复默认安装。 |
| `FontRuntimeRequestRegistry` / pre-register | 历史辅助链路 | 能区分“登记”“命中”“未命中”；纯登记可出现但后续没有真实 `HookHandler` redirect。 | 不再作为当前成功统计来源。 |
| MText 候选扫描 / `MTextInlineFontScanner` | 历史辅助链路 | 扫描可发现候选，但候选发现不是 native 映射；扫描阶段应只读。 | 不恢复为默认修复路径，不写改 `MText.Contents`。 |
| `MText.Contents` 改写 / TrueType 转 SHX | 已证伪/已排除 | 旧链路能修改内容，但会改变 DWG 内容语义，并把缺失内联 TrueType 转成 SHX。 | 不作为当前或推荐修复机制。 |

## 当前默认链路

### 执行顺序

`ExecutionController.Execute` 的当前顺序是：

1. 建立 `DocumentContext`，开始 `RuntimeMappingStateScope`，记录 `LdFileHook` / `ShpLoadHook` 的计数基线。
2. `FontDetector.DetectMissingFonts` 只读检测样式表原始缺失字体，并通过 `DocumentContextManager.StoreDetectionResults` 保存原始结果。
3. `FontReplacer.ReplaceMissingFonts` 执行样式表最终写回。样式表里的 SHX 主字体、SHX 大字体、普通 TrueType、需要写回的样式表 `@TrueType` 都由这里永久修复。
4. 再次 `FontDetector.DetectMissingFonts`，通过 `StoreStillMissingResults` 保存替换后仍缺失的样式，用于 AFRLOG 标记。
5. 如果发生样式表写回，`MarkAffectedTextGraphicsModified` 标记受影响文字、属性和块引用。
6. `Editor.Regen()` 触发 AutoCAD 原生展开与绘制流程，让内联字体进入文件级加载路径。
7. `FontRuntimeMappingStore.GetRuntimeMappingResults` 采集真实文件级 Hook 写入的运行时映射结果，并通过 `StoreRuntimeFontMappingResults` 保存给 UI。
8. 写入命令行统计和 JSONL 诊断，包括本次 `ldFileRedirects` / `shpLoadRedirects` 增量。

这个顺序意味着：样式表永久修复先发生，Hook 只负责后续 Regen 过程中出现的内联运行时字体文件加载。不要恢复“写回前 Regen”“先候选扫描再样式表替换”或“UI 层重新推导 Hook 成功项”的旧描述。早期调查中的旧顺序只能作为历史比较，不能覆盖当前源码事实。

### 安装边界

- `NativeFontHookProfile` 是版本壳提供给 AutoCAD 共享层的 native 入口 profile。
- AutoCAD 2018-2027 版本壳都提供 `ldfile` 和 `shpload` 导出名；缺少 profile 或目标被禁用时必须跳过安装。
- `ldfile` 导出名保持 `?ldfile@@YAHPEB_WHPEAVAcDbDatabase@@PEAVAcFontDescription@@@Z`。
- AutoCAD 2018-2026 的 `shpload` 使用 `_N00HH`，当前实现按 `_N00HH int/int` ABI 安装。
- AutoCAD 2027 的 `shpload` 使用 `_N0022`，当前实现按 `_N0022 bool/bool` ABI 安装，不能用 2018-2026 delegate 强装。
- 导出名缺失、入口 prefix 不匹配、prologue 扫描失败时 fail-closed；RVA 只作为版本指纹和漂移诊断，不是强行安装依据。

## 当前默认保留：`LdFileHook`

### 责任

`LdFileHook` 只处理 AutoCAD `ldfile` 阶段的 SHX 文件级加载请求。它覆盖的主要场景是：

- SHX 主字体加载。
- SHX 大字体加载。
- 内联 `@SHX` 请求，先保留原始请求语义，再尝试去掉 `@` 后按基础 SHX 检查。
- 基础 SHX 不可用时，按配置映射到主字体或大字体替代项。

### 已证明事实

- `LdFileHook` 看到的是进入 AutoCAD `ldfile` native 入口的请求，不是“所有字体请求”。
- `param2=0` / `param2=4` 是当前实现认可的普通字体和大字体加载槽位。
- `param2=2` 是 shape file 槽位，必须直接放行，不能被当成缺失字体修复。
- `@gbcbig.shx` 调查证明了真实 bridge 执行：日志同时出现 pre-register、`LdFileHook.HookHandler`、folded/native request 和最终 `Redirects=946`。
- `baseAlias` 是 CAD 折叠请求的兼容桥，不是不同字体替换；必须结合 `HookHandler` 和 redirect 计数判断是否真实执行。

### 当前源码边界

- `HookHandler` 会先跳过空请求、已可用字体、TrueType 请求和非 SHX 加载请求。
- 映射成功时会替换传给 trampoline 的 `fileName` 指针，递增 `_redirectCount`，写入 `RedirectLog`，并调用 `FontRuntimeMappingStore.RecordRuntimeMapping`。
- 映射失败但属于缺失 SHX 候选时，会调用 `RecordFailedRuntimeMapping`，这只能说明 Hook 看到了请求，不能说明完成了替换。

### 能证明什么

可以用这些信息证明 SHX 文件级映射成功：

- JSONL 中存在 `module=LdFileHook`、`operation=HookHandler`、`status=OK` 的 `执行 SHX 字体加载重定向`。
- 字段里能看到 `original`、`replacement`、`request`、`param2`、`dbScope`。
- 本次命令统计里的 `ldFileRedirects` 大于 0。
- AFRLOG / AFRVIEW 的运行时映射结果来源是 `FontRuntimeMappingStore`，来源列为 `LdFileHook`。

### 不能证明什么

- `LdFileHook` 安装成功不能证明有 SHX 请求进入。
- `ldfile` 命中不能证明 TrueType / `@TrueType` 被处理。
- TrueType 预登记不能证明它进入了 `ldfile`。历史日志显示普通 TrueType 可被检测或预登记，但 `LdFileHook` 仍可能 `hits=0, redirects=0`。
- `param2=2` 命中不能证明字体缺失，它属于 shape 文件放行边界。
- 早期登记、候选扫描、MText 文本内容分析都不能替代真实 redirect 证据。

## 当前默认保留：`ShpLoadHook`

### 责任

`ShpLoadHook` 只处理 AutoCAD `shpload` 阶段 confirmed TrueType / `@TrueType` 的文件级加载请求。它存在的原因是 `LdFileHook` 不能覆盖 TrueType face 请求，尤其不能覆盖 `@TrueType` 这类垂直字体请求。

### 已证明事实

- `shpload` 是当前 TrueType / `@TrueType` 文件级候选执行点。
- 入站样本需要同时记录 `fileName`、`param2`、`arg5`、`arg6`、`charset`、`pitch`、`family` 和 ABI。
- 历史调查证明 TrueType 信息可能出现在 `arg6`、`fileName`、`arg5`，其中 `arg6` 是必须保留的 TrueType-facing 位置。
- AutoCAD 2018-2026 与 2027 的 `shpload` ABI 不同，2027 必须走 `_N0022 bool/bool` 分支。

### 当前源码边界

- 当前处理优先级是 `arg6`、`fileName`、`arg5`。只要某个参数生成 confirmed TrueType redirect，就只替换对应 native 参数。
- 直接 TrueType 文件名可以进入处理，例如 `.ttf` / `.ttc` / `.otf` 等 TrueType 文件请求。
- SHX-like 请求、`.shx` 请求、非 TrueType 扩展名请求必须放行。
- `fileName` / `arg5` 上的 `param2=0/4` 无扩展名请求按 SHX 主字体/大字体槽位放行，不能兜底成 TrueType。
- `arg6` 给出无扩展名字体名时，可以按 confirmed TrueType 路径尝试。
- 对普通 TrueType，替换目标来自配置的 TrueType 字体。
- 对 `@TrueType`，运行时映射保留 `@` 语义，使用配置刷新或 Hook 初始化阶段预解析出的 `@TrueType` 专用目标；Hook 回调内不做 GDI 枚举。
- `TrueTypeStrictBypass` 表示 `ShpLoadHook` 看到了请求但按严格规则放行，不应被 UI 误报成替换成功。

### 能证明什么

可以用这些信息证明 TrueType 文件级映射成功：

- JSONL 中存在 `module=ShpLoadHook`、`operation=HookHandler` 的 `shpload 入站命中`，并能看到实际 ABI、参数和样本。
- 同一运行里存在 `shpload TrueType 重定向`，字段包含 `argument`、`original`、`replacement`、`param2`、`reason`。
- 本次命令统计里的 `shpLoadRedirects` 大于 0。
- AFRLOG / AFRVIEW 的运行时映射结果来源是 `FontRuntimeMappingStore`，来源列为 `ShpLoadHook`。
- AutoCAD 2027 日志显示 `_N0022 bool/bool` 分支，2018-2026 日志显示 `_N00HH int/int` 分支。

### 不能证明什么

- `shpload` 安装成功不能证明 TrueType 请求已替换。
- `shpload 入站命中` 不能证明替换成功；它可能只产生 `TrueTypeStrictBypass`。
- 未知无扩展名、SHX-like、`fileName/arg5 + param2=0/4` 不能作为 TrueType 缺失字体兜底处理。
- `mapFont` 入站或登记不能证明 `shpload` 已经 redirect。

## 历史已证明但已移除：`StyleTextStyleHook`

`StyleTextStyleHook` 曾在 `AcGiTextStyle::loadStyleRec` 上承担样式表运行时映射。

已证明事实：

- 样式表 SHX / TrueType 缺失曾能通过运行时 Hook 映射覆盖。
- 仅等待自然显示链路触发 `loadStyleRec` 不够稳定；对 `10_TrueType竖写字体格式.dwg`，主动 `TextStyle.FromTextStyleTableRecord(id)` 加 `LoadStyleRec` 后，对账达到 `Hook命中缺失槽位=5`、`未命中=0`。
- 样式表主动加载会意外触发 MText setter 日志，因此需要 `StyleTextStyleHook.EnterStyleRuntimeOperation()` / `IsInsideStyleRuntimeOperation` 把 MText Hook 旁路掉。

没有证明：

- 没有证明来源级样式 Hook 应长期作为默认修复路径。
- 没有证明样式表运行时映射可以替代当前样式表永久写回。

当前状态：

- 当前源码已删除该 Hook 的默认安装、编译和执行路径。
- 如果未来恢复，必须重新定义它与 `FontReplacer`、`MTextInlineFontHook`、`LdFileHook` / `ShpLoadHook` 的职责边界，并用真实 DWG 日志证明收益。

## 历史已证明但已移除：`MTextInlineFontHook`

`MTextInlineFontHook` 曾通过 `AcGiTextStyle::setFont`、`setFileName`、`setBigFontFileName` 等 setter 路径处理 MText 内联字体。

已证明事实：

- 旧 `SetFontHookHandler` 能在缺失 TrueType 场景计算 replacement，调用 `FontRuntimeMappingStore.RecordInlineMapping(...)`，并把 `replacementPtr` 传给 trampoline。
- 因为替换发生在活动 `Regen()` / expand 周期，显示结果可能在 Hook 卸载后仍保留在 CAD 图形缓存中。
- 真实日志证明 `setFont`、`setFileName`、`setBigFontFileName` 可以命中；后续 `shpload` 也可以参与 TrueType 文件级路径。
- `MTextInlineFontScanner` 的可靠职责是只读发现候选，而不是在扫描阶段发明语义映射或写改内容。

没有证明：

- 没有证明 direct setter replacement 应恢复为当前默认路径。
- 没有证明 `MText.Contents` 改写或内联 TrueType 转 SHX 是可接受的修复机制。
- 没有证明扫描到候选就等于 native 映射成功。

当前状态：

- 当前源码已删除该 Hook 的默认安装、编译和执行路径。
- 当前 MText 内联映射记录只应来自 `LdFileHook` / `ShpLoadHook` 的真实文件级 redirect。

## 已证伪/已排除：`AcDbMText::explodeFragments`

`AcDbMText::explodeFragments` 曾被怀疑是 MText 字体加载的有效 scope 边界，但真实日志排除了它。

已证明事实：

- `AFR_Diag_20260523_095000.log` 一类真实日志中，`ExplodeFragmentsHits=0`，`ExplodeScope*` 计数也为 0。
- 同一运行里 `SetFontHits=23`、`SetFileNameHits=39`、`SetBigFontFileNameHits=27`、`SetFontDirectTrueTypeBypass=5` 和 `shpload TrueType 重定向` 均出现。

当前结论：

- `explodeFragments` 不是该 MText 字体加载流程的有效边界。
- 不要为了概念完整性保留 dormant scope hook。

## 实验未证明映射成功：`MapFontDiagnosticHook`

`MapFontDiagnosticHook` / `mapFont` 只证明过“可以作为早期登记/诊断实验点”，没有证明为稳定映射成功链路。

已证明事实：

- 临时实验中，`mapFont` 可观察请求并记录 `MapFontEarlyRegister`。
- 实验设计要求 `MapFontDiagnosticHook` 不改 trampoline 参数，只做早期登记。
- 构建和 diff-check 可通过。

没有证明：

- 没有目标 DWG 运行证明 `MapFontDiag.EarlyRegister` 后续一定进入 `LdFileHook` / `ShpLoadHook` 并 redirect。
- 没有证明 `mapFont` 比 `shpload` 更适合作为当前 TrueType 文件级映射入口。

当前结论：

- `mapFont` 只保留为历史候选和诊断经验，不恢复默认安装。
- 任何未来恢复都必须先证明真实 `HookHandler` hit、redirect 计数和最终显示结果。

## 历史辅助链路：登记、候选和请求表

`FontRuntimeRequestRegistry`、pre-register、MText 候选扫描和类似登记表曾用于连接来源 Hook 与文件级 Hook，但它们只能证明“候选被记录”，不能证明“字体已映射”。

已证明事实：

- 纯登记可以出现 `登记=3, 命中=2, 未命中=1` 这样的对账结果。
- `@TrueType` 曾出现“Regen 前登记成功但未进入真实 `LdFileHook.HookHandler`”的情况。
- AFRLOG 不应把 registered-but-unhit 候选提升为成功映射。

当前结论：

- 当前运行时映射结果只接受 `FontRuntimeMappingStore` 中由真实 `HookHandler` redirect 写入的记录。
- UI 层不得根据登记表、候选扫描或字体名自行推导 Hook 成功项。

## 已删除链路与禁止恢复项

以下链路不属于当前 AFR 字体 Hook 维护范围：

- 来源级样式 Hook 作为默认修复路径。
- MText 来源 Hook 或 setter 直接替换作为默认修复路径。
- MText 候选扫描作为默认修复路径。
- `MText.Contents` 改写或内联 TrueType 转 SHX。
- `MapFontDiagnosticHook` 默认安装。
- `FontRuntimeRequestRegistry` 作为成功统计来源。
- DBText / GlyphCore / WenShu / AI / 训练 / 补绘链路。

如果未来要恢复任何一项，必须先重新定义代码边界、数据来源、发布边界和隐私边界，并通过真实 DWG、真实 CAD、真实部署 DLL 证明它优于当前文件级 Hook 路径。

## CAD 实测验收清单

调试 Hook 问题时按这个顺序看证据：

1. 确认 AutoCAD 加载的是本次构建或本次部署的真实 DLL 路径。
2. 打开最新 `AFR_Diag_*.jsonl`，先按 `seq` 还原执行时间线。
3. 查 `ExecutionController` 的 `DetectMissingFonts`、`ReplaceMissingFonts`、`PostDetectMissingFonts`、`InlineRuntimeMappingRegen`、`CollectRuntimeMappingResults`。
4. 查 `FAIL` / `SKIP`，尤其是 `Install`、`HookHandler`、`TrueTypeStrictBypass`、入口 prefix、prologue 扫描和 ABI 分支。
5. 对 SHX 图纸，要求看到 `LdFileHook.HookHandler` 的 redirect 和 `ldFileRedirects > 0`。
6. 对 TrueType / `@TrueType` 图纸，先看 `ShpLoadHook.HookHandler` 入站样本，再看是否有 `shpload TrueType 重定向` 和 `shpLoadRedirects > 0`。
7. 对 AutoCAD 2027，额外确认 `_N0022 bool/bool` 分支；不能出现按 `_N00HH int/int` delegate 调用 2027 入口的迹象。
8. 如果看到历史 Hook 名称，先判断状态：当前默认保留、历史已证明但已移除、已证伪/已排除、实验未证明映射成功，不能直接套用为当前实现。
9. AFRLOG / AFRVIEW 只能展示 `DocumentContextManager` 保存的检测结果、仍缺失结果和 `FontRuntimeMappingStore` 结果；UI 层不得重新推导 Hook 成功项。

## 修改前检查

改 Hook、执行流程或本文档前，至少完成这些检查：

- 搜索 `LdFileHook` / `ShpLoadHook`，确认修改没有扩大到来源级 Hook 或 MText 改写链路。
- 搜索 `TrueTypeStrictBypass`，确认严格放行理由仍可解释，不把未知无扩展名请求兜底成 TrueType。
- 搜索 `_N00HH` / `_N0022`，确认 2027 ABI 与 2018-2026 ABI 没有混用。
- 搜索 `RecordRuntimeMapping`，确认 AFRLOG / AFRVIEW 的运行时映射数据仍来自真实 Hook 结果。
- 搜索 `StyleTextStyleHook`、`MTextInlineFontHook`、`MapFontDiagnosticHook`、`FontRuntimeRequestRegistry`、`explodeFragments`，确认历史条目没有被误写成当前默认链路。
- 搜索 `MText.Contents`，确认它只出现在历史或禁止恢复语境。
