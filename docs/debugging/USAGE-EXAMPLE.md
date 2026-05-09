# DBText 调查工具使用示例

## 场景 1: 快速探测（推荐首次使用）

### 步骤 1: 准备环境
1. 启动 **AutoCAD 2025**（必须是 acdb25.dll 版本）
2. 确保已加载 **AFR 插件 DEBUG 版本**

### 步骤 2: 运行探针
在 AutoCAD 命令行输入：
```
AFRDBTEXTPROBE
```

### 步骤 3: 查看输出
控制台会显示类似如下输出：
```
=== DBText Code Page 写入路径探针 ===
...
目标模块: acdb25.dll 基址=0x7FF8A0000000
  监控 RVA 0x6CEED4
  监控 RVA 0x6AC1A4
  ...
开始创建 DBText 对象...
DBText 已创建: ObjectId=XXXXXX
AcDbImpText 指针=0x...
code_page_id 字段值=0x28 (GBK)  <- 如果是 0x28，说明需要修复

=== 探针结果汇总 ===
RVA 0x6CEED4: 命中 2 次           <- 这个 RVA 被访问了！
RVA 0x6AC1A4: 命中 1 次
RVA 0x6D2388: 命中 0 次
...
```

### 步骤 4: 记录结果
如果看到 "命中 X 次" 且 X > 0，说明找到了可能的目标！

**记录清单**：
- 命中的 RVA 地址（如 `0x6CEED4`）
- 命中次数
- `code_page_id` 字段值（应为 `0x28` GBK，表示需要修复）

---

## 场景 2: 运行时追踪（用于打开真实图纸）

### 步骤 1: 启动追踪器
```
AFRTRACERSTART
```
控制台显示：
```
启动 DBText Hook 追踪器...
追踪器已启动。请执行以下操作以收集数据:
  1. 打开包含 DBText 的 Big5 图纸
  2. 或使用 AFRDBTEXTPROBE 命令创建测试 DBText
  3. 然后使用 AFRTRACERREPORT 查看追踪报告
```

### 步骤 2: 打开 Big5 图纸
使用 AutoCAD 的 `OPEN` 命令打开一个包含 DBText 的 Big5 图纸。

### 步骤 3: 查看追踪报告
```
AFRTRACERREPORT
```
输出示例：
```
=== DBText Hook 追踪报告 ===
模块基址: 0x7FF8A0000000
监控点数量: 25

命中统计:
  ★ RVA 0x6CEED4:     5 次 - core: code-page family writer (suspected)
  ★ RVA 0x6AC1A4:     2 次 - core: code-page family reader (cache loader)
    RVA 0x6D2388:     0 次 - shared: init orchestrator (6 callers)
    RVA 0x6CFE6C:     8 次 - MText: known working RVA
    ...

说明:
  ★ = 已命中 (可能是 DBText 路径)
    = 未命中 (非 DBText 路径或未触发)
```

### 步骤 4: 停止追踪器
```
AFRTRACERSTOP
```

---

## 场景 3: 调试器断点（进阶用户）

### 前提条件
- 安装了 **Visual Studio 2022+**
- 具备基本的 C++ 调试经验

### 步骤 1: 附加调试器
1. 启动 AutoCAD 2025
2. 在 Visual Studio 中：`调试` > `附加到进程` > 选择 `acad.exe`

### 步骤 2: 设置条件断点
在 Visual Studio 的"即时窗口"中输入（或使用断点窗口）：

**方法 A: 直接地址断点**
```
bp acdb25+0x6CEED4
bp acdb25+0x6AC1A4
bp acdb25+0x6D2388
```

**方法 B: 条件断点（仅 DBText）**
```cpp
// 设置断点条件（需要判断对象类型）
// 示例：检查调用栈是否包含 AcDbText 相关符号
```

### 步骤 3: 触发断点
在 AutoCAD 中打开 Big5 图纸。

### 步骤 4: 记录调用栈

当断点命中时：
1. 查看 `调用堆栈` 窗口
2. 记录完整的调用链（从上到下）
3. 对比与 MText 的差异

**关键信息**：
- 断点地址（RVA）
- 调用栈顶部 5-10 个函数
- 参数值（尤其是 `rcx`/`rdx` 寄存器）

---

## 输出解读

### code_page_id 字段值
- `0x27` (39) = **Big5** ✅ 正确
- `0x28` (40) = **GBK** ❌ 需要修复
- 其他值 = 其他编码，可能无关

### 命中次数
- `0 次` = 该 RVA 未参与 DBText 创建/加载
- `1-3 次` = 可能是目标路径（每个 DBText 对象触发一次）
- `>5 次` = 可能是共享路径（MText + DBText）

### 常见问题

**Q: 所有 RVA 都是 0 次怎么办？**  
A: 可能原因：
1. 图纸中没有 DBText 对象（只有 MText）
2. DBText 使用完全不同的路径（需要扩大扫描范围）
3. 写入时机在更早阶段（Database 事务前）

**Q: 多个 RVA 都有命中怎么办？**  
A: 这是正常的！选择：
1. 命中次数最多的（最有可能是共享路径）
2. 在记忆文件中标记为"suspected"的（如 `0x6CEED4`）
3. 全部尝试（添加到 Hook 候选列表）

**Q: 如何确认 Hook 是否成功？**  
A: 最终验证：
1. 将 RVA 添加到 `CodePageFamilyHook.CandidateRvas`
2. 重新编译并加载插件
3. 打开 Big5 图纸
4. 查看 DBText 是否正确显示（不是乱码）

---

## 报告模板

将你的发现整理成以下格式，方便后续分析：

```
### 调查报告

**日期**: 2026-05-XX  
**AutoCAD 版本**: 2025 (acdb25.dll)  
**测试图纸**: [图纸文件名或描述]  
**测试方法**: [AFRDBTEXTPROBE / AFRTRACERREPORT / 调试器]

**命中 RVA**:
- 0x6CEED4: 5 次
- 0x6AC1A4: 2 次

**code_page_id 值**: 0x28 (GBK) - 需要修复

**调用栈** (如果使用调试器):
```
acdb25.dll+0x6CEED4
acdb25.dll+0xXXXXXX
...
```

**结论**:
发现 RVA 0x6CEED4 参与 DBText 加载，建议添加到 Hook 候选列表。

**下一步**:
1. 验证 0x6CEED4 的 prologue 是否可 Hook
2. 添加到 CandidateRvas 数组
3. 测试修复效果
```

---

**提示**: 
- 第一次调查建议使用 `AFRDBTEXTPROBE`（最简单）
- 如果有真实图纸，使用 `AFRTRACERSTART/REPORT`（更准确）
- 如果需要深度分析，使用 Visual Studio 调试器（最强大）

**祝你调查顺利！** 🔍
