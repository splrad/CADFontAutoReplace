# DBText Big5 乱码问题调查工具

## 问题描述

当前 `CodePageFamilyHook` 已成功修复 MText 的 Big5 乱码问题，但 DBText 仍然乱码。需要找到 DBText 专用的 code-page family 写入路径并扩展 Hook 覆盖范围。

## 调查工具

### 1. AFRDBTEXTPROBE 命令
在 AutoCAD 中运行此命令来创建测试 DBText 并监控候选 RVA。

```
命令: AFRDBTEXTPROBE
功能: 创建包含 Big5 文字的 DBText，监控多个候选 RVA 的命中情况
输出: 哪些 RVA 被访问，以及 code_page_id 字段的值
```

### 2. 追踪器命令组
```
AFRTRACERSTART  - 启动运行时追踪器
AFRTRACERREPORT - 查看追踪报告（显示各 RVA 命中次数）
AFRTRACERSTOP   - 停止追踪器
```

### 3. 调试器断点（进阶）
如果你有 Visual Studio 或 WinDbg，可以在以下地址设置断点：
```
acdb25.dll+0x6CEED4  (核心目标)
acdb25.dll+0x6AC1A4  (cache loader)
acdb25.dll+0x6D2388  (orchestrator)
```

## 快速开始

1. 加载 AFR 插件（DEBUG 版本）
2. 在 AutoCAD 中运行：`AFRDBTEXTPROBE`
3. 查看输出，记录命中的 RVA
4. 将命中的 RVA 添加到 `CodePageFamilyHook.CandidateRvas`

## 候选 RVA 列表

完整列表见 `DbTextHookTracer.cs` 源码，包括：
- 高优先级：0x6CEED4, 0x6AC1A4, 0x6D2388
- 中优先级：0x6D2410, 0x6D2498, 0x6CB79C
- 扩展扫描：0x6CEEC0, 0x6CEEB0, 0x6CEEA0 等

## 源码位置

- `src/AutoCAD/AFR.AutoCAD/FontMapping/DbTextHookTracer.cs` - 追踪器实现
- `src/AutoCAD/AFR.AutoCAD/DebugCommands/DbTextCodePageProbeCommand.cs` - 探针命令
- `src/AutoCAD/AFR.AutoCAD/DebugCommands/DbTextTracerCommand.cs` - 追踪器命令
- `src/AutoCAD/AFR.AutoCAD/FontMapping/CodePageFamilyHook.cs` - Hook 实现

## 预期结果

成功后，DBText 中的 Big5 文字应该：
- 特性面板显示正确的繁体字（不是乱码）
- 图纸显示正常（没有 `?` 字符）

## 参考

详细的逆向分析结果见 `.github/copilot-instructions.md` 中的记忆部分。
