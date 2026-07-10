---
applyTo: "src/**,tools/Publish-ReleaseAssets.ps1,Directory.Build.props,Directory.Packages.props,global.json,Version.props,chore/Fonts.zip"
---

输出语言、严重程度和标题格式统一遵循 `.github/copilot-instructions.md`；本文件只补充运行时代码和发布资产路径的审查重点。

审查插件运行时代码、部署器和发布包内容时，优先检查：

- AutoCAD 版本兼容性、目标框架、WPF 行为和部署器嵌入资源是否被破坏。
- `AFR-ACAD20XX` 插件 DLL、`.cad.json` 描述符、字体包和发布资产名称是否仍能被发布脚本正确发现。
- 字体替换、SHX 处理、运行时映射和部署路径相关变更是否会改变用户可见行为。
- `Version.props`、构建属性和发布脚本变更是否会触发错误版本号、错误 tag 或错误 Release 资产。
- 运行代码相关 PR 才应进入 Release Notes；纯文档、workflow 或版本号维护不应产生用户可见变更项。

只有会造成插件无法加载、部署器无法运行、发布资产缺失、AutoCAD 兼容性回归、用户数据风险或错误 Release 的问题，才标记为 `严重程度：阻断`。
