# DBText Big5 修复实施计划

## 当前进展

### ✅ 已完成
1. **理论分析**：确认托管层事后修复不可行（数学上不可逆）
2. **MText 修复**：`CodePageFamilyHook` 已成功覆盖 MText 反序列化路径
3. **调查工具**：创建了完整的调试工具链

### ❌ 待完成
- **DBText 修复**：需要找到 DBText 的 code-page family 写入路径

## 调查工具已就绪

### 1. 探针命令
```
文件: src/AutoCAD/AFR.AutoCAD/DebugCommands/DbTextCodePageProbeCommand.cs
命令: AFRDBTEXTPROBE
功能: 创建测试 DBText，监控候选 RVA 命中情况
状态: ✅ 已编译通过
```

### 2. 追踪器
```
文件: src/AutoCAD/AFR.AutoCAD/FontMapping/DbTextHookTracer.cs
命令: AFRTRACERSTART / AFRTRACERSTOP / AFRTRACERREPORT
功能: 运行时追踪多个 RVA 的命中次数
状态: ✅ 已编译通过
```

### 3. 调试指南
```
文件: docs/debugging/README-DBText-Investigation.md
内容: 完整的调查流程和候选 RVA 列表
状态: ✅ 已创建
```

## 下一步行动

### 立即执行（无需额外工具）
1. 启动 AutoCAD 2025（acdb25.dll）
2. 加载 AFR 插件（DEBUG 版本）
3. 运行命令：`AFRDBTEXTPROBE`
4. 观察输出，记录任何命中的 RVA

### 如果有 Visual Studio
1. 附加调试器到 AutoCAD 进程
2. 在以下地址设置断点：
   - `acdb25.dll+0x6CEED4`
   - `acdb25.dll+0x6AC1A4`
   - `acdb25.dll+0x6D2388`
3. 打开包含 DBText 的 Big5 图纸
4. 记录命中的断点及其调用栈

### 如果有 IDA Pro / Ghidra
1. 加载 `acdb25.dll` 到反汇编器
2. 搜索 `AcDbImpText` 类符号
3. 追溯 xref 到 `0x6CEED4` 附近的函数
4. 验证函数 prologue 是否可安装 Hook

## 候选 RVA 优先级

基于记忆文件中的静态分析，按优先级排序：

### 🔴 高优先级（最有可能）
- `0x6CEED4`: 核心 code-page writer（记忆文件中多次提及）
- `0x6AC1A4`: code-page reader（负责从全局缓存加载）
- `0x6D2388`: 初始化编排函数（6个调用者）

### 🟡 中优先级
- `0x6D2410`: `0x6D2388` 的对称兄弟（4个调用者）
- `0x6D2498`: 共享 helper（11个调用者，可能同时服务 MText 和 DBText）
- `0x6CB79C`: 编排节点

### 🟢 低优先级（扩展扫描）
- `0x6CEEC0` ~ `0x6CEE70`: `0x6CEED4` 前向扫描区域
- `0x6D1D34` / `0x6D1D7C`: 替代调度器
- `0x6D2654` / `0x6CE334` / `0x6CE3BC`: 字符读取器

完整列表见 `DbTextHookTracer.cs` 源码。

## 成功标准

找到正确的 RVA 后，应满足以下条件：

### 功能验证
- ✅ DBText 中的 Big5 文字正确显示（不是乱码）
- ✅ 特性面板显示正确的繁体字
- ✅ 图纸中没有 `?` 字符
- ✅ GBK 系统下打开 Big5 图纸无异常

### 技术验证
- ✅ Hook 命中计数 > 0（通过 `AFRTRACERREPORT` 查看）
- ✅ `[AcDbImpText + 0x46C]` 字段值为 `0x27`（Big5），而非 `0x28`（GBK）
- ✅ 函数 prologue 可安装 Hook（无 RIP-relative 指令）

## 实施步骤

一旦找到正确的 RVA：

### 步骤 1: 添加到 Hook
编辑 `src/AutoCAD/AFR.AutoCAD/FontMapping/CodePageFamilyHook.cs`：
```csharp
private static readonly uint[] CandidateRvas = [
    // ... 现有 RVA ...
    0xXXXXXX,  // DBText: <描述来源和验证结果>
];
```

### 步骤 2: 测试验证
1. 重新编译插件（DEBUG 版本）
2. 加载到 AutoCAD 2025
3. 打开 Big5 图纸（包含 DBText）
4. 验证 DBText 是否正确显示

### 步骤 3: 更新文档
1. 在 `.github/copilot-instructions.md` 记录发现
2. 更新本文档的成功案例
3. 添加 RVA 到候选列表并标记状态

## 风险与应对

### 风险 1: 所有候选 RVA 均未命中
**可能原因**：
- DBText 使用完全不同的 code-page 写入机制
- 字段偏移不是 `+0x46C`
- 写入时机在更早阶段（Database 事务前）

**应对方案**：
1. 使用内存扫描工具（Cheat Engine）定位字段
2. 在 IDA Pro 中搜索所有写入 `0x46C` 偏移的指令
3. 扩大 RVA 扫描范围到整个 `.text` 段

### 风险 2: 命中但仍乱码
**可能原因**：
- RVA 的 prologue 包含 RIP-relative 指令，Hook 失败
- 同一函数被多次调用，需要条件判断

**应对方案**：
1. 使用记忆文件中的 PowerShell 脚本验证 prologue
2. 在 Hook handler 中添加对象类型检测（区分 MText/DBText）
3. 向前/向后扫描寻找更合适的 Hook 点

### 风险 3: AutoCAD 崩溃
**可能原因**：
- Hook 破坏了函数调用约定
- trampoline 地址计算错误

**应对方案**：
1. 先使用"仅监控"模式（不实际修改字段）
2. 检查 prologue size 是否正确
3. 降级到仅覆盖 MText（放弃 DBText 修复）

## 时间估算

- **快速路径**（候选 RVA 命中）：1-2 小时
- **深度调查**（需要静态分析）：4-8 小时
- **最坏情况**（需要重新逆向）：1-2 天

## 参考资料

- `.github/copilot-instructions.md`: 完整逆向分析记忆
- `src/AutoCAD/AFR.AutoCAD/Services/DbTextEncodingRepairService.cs`: 为何事后修复不可行
- `src/AutoCAD/AFR.AutoCAD/FontMapping/CodePageFamilyHook.cs`: 当前 Hook 实现

---

**创建日期**: 2026-05  
**状态**: 调查工具已就绪，等待执行  
**负责人**: 参考记忆文件中的调查记录
