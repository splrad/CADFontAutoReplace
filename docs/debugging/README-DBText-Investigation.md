# DBText DBCS Code Page 调查工具

## 当前实现

DBText/MText 的 DBCS 修复不再依赖 DWG 文件头猜测或文字外观判断。DEBUG 构建会安装三层原生 Hook：

- `DwgFilerCodePageScopeHook`: 通过 `acdb25.dll` 导出符号解析并 hook `AcDbMemoryDwgFiler::readString(AcString&)` 和 `readString(wchar_t**)`，读取当前 DWG filer 的真实 `code_page_id`。
- `TextEditorDbcsDecodeHook`: 通过导出符号 hook `TextEditor::read_doublebyte(char const*, wchar_t&, code_page_id)`，在当前线程处于 readString scope 时把错误的系统 code page 参数修正为 filer code page。
- `CodePageFamilyHook`: 在运行时 `.text` 段中用唯一长签名定位文本解码上下文初始化函数，在初始化后把 `[context+0x46C]` 修正为当前 readString scope 中的 DBCS code page。

仅 AutoCAD 2025 的 `acdb25.dll` 导出符号和 code-page context 签名已验证。其他版本必须重新确认导出签名、入口字节和 `.pdata` 范围。

## 命令

```
AFRDBTEXTPROBE   输出 readString scope、TextEditor DBCS decode 与 code-page context hook 统计
AFRTRACERSTART   启用追踪报告标记
AFRTRACERREPORT  输出同一份真实 hook 统计
AFRTRACERSTOP    停用追踪报告标记
```

`AFRDBTEXTPROBE` 不再创建测试 DBText。新建对象不会经过 DWG readString 反序列化链路，不能证明目标图纸加载时的 code page。

## 验证流程

1. 重新生成 `AFR-ACAD2025` Debug 插件。
2. 完全关闭并冷启动 AutoCAD 2025，确保 Hook 在打开问题 DWG 前已安装。
3. 手动打开目标 DWG。
4. 运行 `AFRDBTEXTPROBE` 或 `AFRTRACERREPORT`。
5. 检查报告是否包含：
   - `LastCodePageId: 0x27(GBK/CP936 observed)`
   - `LastOriginalCodePageId: 0x28(Big5/CP950 observed)`
   - `LastPatchedCodePageId: 0x27(GBK/CP936 observed)`
   - `TextEditor DBCS Decode Hook` 或 `Code Page Family Hook` 的 `PatchedCount` 大于 0

若 `LastCodePageId` 不是 `0x27`，说明 DWG filer 没有提供目标图纸所需的 CP936 证据；在无误杀约束下不能自动按 CP936 修复。
