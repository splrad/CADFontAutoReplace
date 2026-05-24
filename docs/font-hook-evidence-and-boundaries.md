# 字体 Hook 证据与边界

本文记录当前已找到的字体 Hook 证据、作用和保留/删除原因。当前默认架构只把 `LdFileHook` 和 `ShpLoadHook` 放入修复链路；`mapFont` 只保留为 Debug 证据对象；`StyleTextStyleHook` 和 `MTextInlineFontHook` 已从默认安装、编译和执行路径删除。

## 当前执行顺序

1. 清理上一文档运行时映射结果和文件级 Hook 诊断基线。
2. 只读检测样式表原始缺失字体，并保存原始检测结果。
3. 在样式表永久写回前执行 `Editor.Regen()`，让文件级 Hook 先看到原始字体加载请求。
4. 只采集 `LdFileHook.HookHandler` / `ShpLoadHook.HookHandler` 真实 redirect 写入的 `FontRuntimeMappingStore` 结果。
5. 最后执行样式表永久替换，处理仍需要写回的普通缺失字体和 `@SHX`。
6. 二次检测、必要最终刷新、写入 AFRLOG 和命令行统计。

预登记、扫描候选、`mapFont` 入站样本都不是映射成功证据。成功只能由真实文件级 Hook 命中、redirect 计数和运行时映射结果共同证明。

## `LdFileHook`

保留原因：

- 已有 CAD 实测显示 `LdFileHook.HookHandler` 能看到 SHX 文件级加载请求，并产生非零 redirect。
- 9.1 的有效边界是文件级 SHX 请求处理，而不是来源 Hook 预登记。
- 当前实现继续使用 `NativeInlineHook` 和 `NativeFontHookProfile` 做版本 profile、RVA 和前缀校验，避免回退旧手写 trampoline。

职责：

- 处理 `ldfile` 的 SHX 主字体和大字体请求。
- 使用 `param2` 区分普通字体、大字体和 shape 文件：`param2=2` shape file 必须跳过。
- 对 `@SHX` 请求先保留原始语义，再尝试去 `@` 后基础 SHX；基础不可用时才映射配置字体。
- 写入真实 redirect 日志和 `FontRuntimeMappingStore` 结果。

边界：

- 不处理 TrueType face。
- 不把预登记或候选扫描视为成功。
- 不恢复 MText setter 直接替换或 `MText.Contents` 改写。

## `ShpLoadHook`

保留原因：

- `LdFileHook` 不能覆盖 TrueType / `@TrueType` face 请求。
- `shpload` 比 `mapFont` 更接近文件级加载执行点，适合作为 TrueType 的默认运行时映射边界。
- 现有证据表明 TrueType 信息可能出现在 `fileName`、`arg5`、`arg6` 中，尤其需要采样 `arg6`。

职责：

- 在 `shpload` 阶段采样 `fileName`、`arg5`、`arg6`，但只有确认是 TrueType 的请求才允许替换。
- 对缺失普通 TrueType 映射到配置 TrueType。
- 对缺失 `@TrueType` 保留 `@` 前缀，映射到 `@` + 配置 TrueType。
- 只有实际替换 native 参数并调用 trampoline 时，才记录 redirect 和运行时映射。

边界：

- 不处理 `.shx`、已知 SHX、可归一化为已知 SHX 的无扩展名请求。
- 不把 `fileName` / `arg5` 上 `param2=0/4` 的无扩展名请求兜底成 TrueType；这类请求默认属于 SHX 主字体/大字体加载槽位。
- 不把未知无扩展名当作缺失 TrueType。
- 不再判断 `@TrueType` vertical face 是否被系统枚举；运行时只按去掉 `@` 后的基础 TrueType 是否存在决定保留原请求或映射到 `@` + 配置 TrueType。
- 不依赖 `FontRuntimeRequestRegistry` 作为默认修复前置条件。

## `mapFont`

当前证据：

- `mapFont` 可以更早看到部分字体名入站样本。
- 入站样本包含噪声，且距离最终文件级加载仍有差距。
- 早期登记实验只证明了可能的上游观测点，没有证明它可以替代文件级 redirect。

当前结论：

- 不进入默认安装链路。
- 不作为默认修复点。
- 可以在 Debug 中作为可选诊断，观察上游入站样本、参数形态和噪声。
- `mapFont` 样本或预登记不能写入 AFRLOG 的成功映射统计。

## `StyleTextStyleHook`

历史作用：

- 曾用于样式表来源的缺失 `@TrueType` 识别和运行时登记。
- 曾通过样式加载流程把请求推给下游文件级 Hook。

删除原因：

- 多行文字问题定位过程中，来源 Hook 与文件级执行边界混合，增加了判断成本。
- 当前目标是恢复 9.1 文件级 Hook 边界，先让原始字体请求进入 `LdFileHook` / `ShpLoadHook`。
- 样式表永久替换必须放在运行时映射之后，不能依赖来源 Hook 提前改变请求。

当前状态：

- 不编译。
- 不安装。
- 不参与文档执行流程。

## `MTextInlineFontHook`

历史作用：

- 曾用于 MText 内联字体候选识别、预登记和 setter 层运行时替换。
- 曾辅助多行文字内联字体在 Regen 过程中产生映射记录。

删除原因：

- `MText.Contents` 改写和 setter 直接替换容易绕过文件级 Hook，导致成功标准与真实加载边界不一致。
- 当前实测验收要求只看文件级 `HookHandler` redirect 和非零计数。
- 多行文字仍应由 CAD 原生加载流程触发 `LdFileHook` / `ShpLoadHook`，不能恢复内容重写。

当前状态：

- 不编译。
- 不安装。
- 不参与文档执行流程。
- 不恢复 `MText.Contents` 改写。

## CAD 实测检查点

- 启动日志默认只安装 `LdFileHook` 和 `ShpLoadHook`。
- `mapFont` 默认没有安装日志。
- 样式表永久替换日志必须出现在运行时映射采集之后。
- SHX 图纸要求 `LdFileHook.HookHandler` 有真实 redirect，且 `ldfile redirects > 0`。
- TrueType / `@TrueType` 图纸要求 `ShpLoadHook` 有真实入站样本；若映射成功，要求 `shpload redirects > 0`。
- AFRLOG 的运行时映射数量只来自 `FontRuntimeMappingStore` 中真实文件级 Hook 命中结果。
