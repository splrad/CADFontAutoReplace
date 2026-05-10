# DBText DBCS Hook 使用示例

## 快速验证

1. 启动 AutoCAD 2025，并确认加载的是 `AFR-ACAD2025` Debug 插件。
2. 手动打开包含单行文字乱码的目标 DWG。
3. 运行：

```
AFRDBTEXTPROBE
```

或：

```
AFRTRACERREPORT
```

## 预期输出

报告应类似：

```
=== DWG Filer Code Page Scope ===
Installed: True
AcStringReadStringHits: 12
WideCharPointerReadStringHits: 4
LastCodePageId: 0x27(Big5/CP950)
LastIsDoubleByte: True

=== TextEditor DBCS Decode Hook ===
Installed: True
HitCount: 20
PatchedCount: 8
LastOriginalCodePageId: 0x28(GBK/CP936)
LastFilerCodePageId: 0x27(Big5/CP950)
LastPatchedCodePageId: 0x27(Big5/CP950)

=== Code Page Family Hook ===
Installed: True
HitCount: 0
PatchedCount: 0
LastReturnRva: 0x6D23C1
```

这表示 DWG filer 声明为 Big5，但 AutoCAD 在 DBCS 字节解码点传入了系统 GBK，Hook 已在反序列化期间把解码入参修正为 Big5。单行文字图纸可能只命中 `TextEditor DBCS Decode Hook`，不命中 `Code Page Family Hook`。

## 异常解读

- `Installed: False`: 当前 CAD 版本的导出符号或 code-page context 签名未验证，或入口字节不匹配。
- `PatchedCount: 0` 且 `SameCodePageCount` 增加：DWG code page 与系统上下文一致，不需要修复。
- `NoDbcsScopeCount` 增加：DBCS 解码或 code-page context 初始化函数命中时不在 DWG readString 解码作用域内，不会自动 patch。
- `LastCodePageId` 不是 `0x27`: 当前 DWG filer 没有提供 Big5 证据，不能在无误杀约束下按 Big5 修复。

## 调试器断点

需要进一步验证时，可在 Visual Studio 中附加 `acad.exe` 并观察：

断点地址以日志中的“导出解析成功”或“签名解析成功”RVA 为准，例如：

```
AcDbMemoryDwgFiler.readString(AcString&) 导出解析成功。RVA=0x...
AcDbMemoryDwgFiler.readString(wchar_t**) 导出解析成功。RVA=0x...
TextEditor::read_doublebyte(char*) 导出解析成功。RVA=0x...
TextEditor code-page context init 签名解析成功。RVA=0x...
```

断点必须覆盖“手动打开目标 DWG”之后的加载过程；打开完成后再命中的文本 API 通常已经太晚。
