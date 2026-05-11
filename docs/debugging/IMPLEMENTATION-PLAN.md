# DBText DBCS 修复实施状态

## 已实施

- 用 `DwgFilerCodePageScopeHook` 通过 `acdb25.dll` 导出符号解析 `AcDbMemoryDwgFiler::readString` 两个重载，并建立 DWG filer code page 作用域。
- 用 `TextEditorDbcsDecodeHook` 通过导出符号 hook `TextEditor::read_doublebyte(char const*, wchar_t&, code_page_id)`，在 DBText 的实际 DBCS 字节解码点按 readString scope 修正 code page 入参。
- 用 `CodePageFamilyHook` 在运行时 `.text` 段中唯一签名定位 code-page context 初始化函数，原函数返回后按当前 readString scope 的 DBCS `code_page_id` 修正 `[context+0x46C]`。
- 移除 `DocumentCreateStarted` 文件头预判链路，不再读取 `header[0x13]`，不依赖 `DWGCODEPAGE` 或乱码外观。
- `AFRDBTEXTPROBE` / `AFRTRACERREPORT` 改为输出真实 hook 统计，不再显示未安装探针的候选 RVA 命中计数。

## 验证标准

- 目标 DWG：`LastCodePageId` 为 `0x27(GBK/CP936 observed)`，`LastOriginalCodePageId` 为 `0x28(Big5/CP950 observed)`，`LastPatchedCodePageId` 为 `0x27`，`TextEditorDbcsDecodeHook` 或 `CodePageFamilyHook` 的 `PatchedCount > 0`。
- 正常同 code page DWG：`LastCodePageId` 与 `LastOriginalCodePageId` 一致，不得发生跨 code page patch。
- 非 DBCS / 无 readString 作用域：只增加 `NoDbcsScopeCount` 或不 patch。

## 版本限制

当前导出符号和 code-page context 签名只对 AutoCAD 2025 的 `acdb25.dll` 验证。AutoCAD 2026 也使用 `acdb25.dll` 名称，但不得复用 2025 签名，必须重新静态确认入口字节和 `.pdata` 范围。
