# 字体 Hook 证据与边界

本文记录当前保留的字体 Hook 证据、职责和验收边界。当前默认架构只把 `LdFileHook` 和 `ShpLoadHook` 放入修复链路；已删除的上游诊断点、来源级 Hook 和候选扫描链路不再作为默认修复路径存在。

## 当前执行顺序

1. 清理上一文档运行时映射结果和文件级 Hook 诊断基线。
2. 只读检测样式表原始缺失字体，并保存原始检测结果。
3. 在样式表永久写回前执行 `Editor.Regen()`，让文件级 Hook 先看到原始字体加载请求。
4. 只采集 `LdFileHook.HookHandler` / `ShpLoadHook.HookHandler` 真实 redirect 写入的 `FontRuntimeMappingStore` 结果。
5. 最后执行样式表永久替换，处理仍需要写回的普通缺失字体和 `@SHX`。
6. 二次检测、必要最终刷新、写入 AFRLOG 和命令行统计。

早期登记、候选扫描和上游入站样本都不是映射成功证据。成功只能由真实文件级 Hook 命中、redirect 计数和运行时映射结果共同证明。

## `LdFileHook`

保留原因：

- CAD 实测显示 `LdFileHook.HookHandler` 能看到 SHX 文件级加载请求，并产生非零 redirect。
- 当前有效边界是文件级 SHX 请求处理。
- 当前实现继续使用 `NativeInlineHook` 和 `NativeFontHookProfile` 做版本 profile、RVA 和前缀校验。

职责：

- 处理 `ldfile` 的 SHX 主字体和大字体请求。
- 使用 `param2` 区分普通字体、大字体和 shape 文件：`param2=2` shape file 必须跳过。
- 对 `@SHX` 请求先保留原始语义，再尝试去 `@` 后基础 SHX；基础不可用时才映射配置字体。
- 写入真实 redirect 日志和 `FontRuntimeMappingStore` 结果。

边界：

- 不处理 TrueType face。
- 不把早期登记或候选扫描视为成功。
- 不恢复 MText setter 直接替换或 `MText.Contents` 改写。

## `ShpLoadHook`

保留原因：

- `LdFileHook` 不能覆盖 TrueType / `@TrueType` face 请求。
- `shpload` 是当前 TrueType 文件级加载执行点。
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
- 不依赖任何运行时请求登记表作为默认修复前置条件。

## 已删除边界

旧上游诊断点、来源级样式 Hook、MText 内联候选扫描和旧 Debug 展示管线已经从源码删除。当前验收不再检查这些入口，也不允许它们的样本写入 AFRLOG 成功统计。

## CAD 实测检查点

- 启动日志默认只安装 `LdFileHook` 和 `ShpLoadHook`。
- 样式表永久替换日志必须出现在运行时映射采集之后。
- SHX 图纸要求 `LdFileHook.HookHandler` 有真实 redirect，且 `ldfile redirects > 0`。
- TrueType / `@TrueType` 图纸要求 `ShpLoadHook` 有真实入站样本；若映射成功，要求 `shpload redirects > 0`。
- AFRLOG 的运行时映射数量只来自 `FontRuntimeMappingStore` 中真实文件级 Hook 命中结果。
