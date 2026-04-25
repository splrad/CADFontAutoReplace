<div align="center">

# AFR — CAD 缺失字体自动替换工具

**打开图纸不再出现文字不显示、乱码，所有缺失字体自动搞定**

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE.txt)
[![AutoCAD](https://img.shields.io/badge/AutoCAD-2026-red.svg)](#-已支持版本)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)

[GitHub](https://github.com/splrad/CADFontAutoReplace) · [Gitee（国内镜像）](https://gitee.com/splrad/CADFontAutoReplace) · [Releases](https://github.com/splrad/CADFontAutoReplace/releases) · [提交 Issue](https://github.com/splrad/CADFontAutoReplace/issues)

**📥 补充下载链接：[蓝奏云网盘](https://wwbcq.lanzouv.com/b01jt1w7of) （密码: exsc）**

</div>

---

## ✨ 项目亮点

### 核心能力

- 🚀 **自动化执行**：配置一次后，后续打开图纸自动检测并替换缺失字体。
- 🎯 **三类字体全覆盖**：支持 `SHX 主字体`、`SHX 大字体`、`TrueType` 缺失修复。
- 🧠 **类型安全替换**：自动区分主字体/大字体，避免替换类型错配。
- 🔧 **双阶段修复链路**：`ldfile Hook` + 样式表替换，兼顾解析阶段与持久化结果。
- 🧾 **MText 乱码专项处理**：对 MText 内联字体（`\F` / `\f`）缺失场景提供重定向修复能力。
- 📦 **单 DLL 部署**：第三方 UI 依赖已嵌入，分发与部署成本低。
- 🖥️ **可视化兜底**：`AFRLOG` 支持逐条调整与批量填充。
- 🔄 **可回退可卸载**：仅修改当前图纸，不强制保存，支持 `AFRUNLOAD`。

### 与常见方案对比

| 常见做法 | 典型短板 | AFR 的增强点 |
|---|---|---|
| 仅依赖 `FONTALT` 回退 | 对复杂场景覆盖有限，人工干预多 | 自动检测 + 自动替换，减少手工操作 |
| 仅改样式表字体 | 对 MText 内联字体覆盖不足 | 同时处理样式表与 MText 内联字体 |
| 只替换 SHX | TrueType 缺失场景处理不完整 | SHX/大字体/TrueType 三类统一纳管 |
| 只做“替换不验证” | 可能写入不可用字体 | 内置替换后二次验证与统计输出 |
| 纯命令行流程 | 批量调整和复核成本高 | 配置窗口 + 日志窗口 + 手动兜底闭环 |

### 工程化补充

- 支持 `AFRVIEW` / `AFRINSERT`（Debug）用于内联字体问题复现与定位。
- 诊断日志覆盖执行阶段、Hook 重定向与替换结果，便于问题排查。

---

## 🖼️ 界面预览

<table>
<tr>
<td align="center"><b>AFR 命令 — 字体配置</b></td>
<td align="center"><b>AFRLOG 命令 — 替换日志</b></td>
</tr>
<tr>
<td align="center"><img src="https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260407005000713.jpg" width="380" /></td>
<td align="center"><img src="https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260407005034079.jpg" width="380" /></td>
</tr>
</table>

---

## ✅ 已支持版本

| DLL 文件名 | AutoCAD 版本 | .NET |
|:---:|:---:|:---:|
| `AFR-ACAD2026.dll` | AutoCAD **2026**（R25.1） | .NET 8 |
| `AFR-ACAD2024.dll` | AutoCAD **2024**（R24.3） | .NET 4.8 |

---

## 🗺️ 开发计划

| 平台 | 版本 | 状态 |
|---|---|---|
| AutoCAD | 2026 | ✅ **已支持** |
| AutoCAD | 2025 | ✅ **已支持** |
| AutoCAD | 2024 | ✅ **已支持** |
| AutoCAD | 2023 | ✅ **已支持** |
| AutoCAD | 2022 | ✅ **已支持** |
| 中望CAD | 2026 | ⬜ 计划中 |
| 中望CAD | 2025 | ⬜ 计划中 |

---

## 🚀 快速开始

> 面向首次使用 AutoCAD 插件的用户；有经验用户可直接查看[命令速查](#️-命令速查)。

### 1) 下载插件

1. 前往 [Releases](https://github.com/splrad/CADFontAutoReplace/releases)
2. 下载与你 AutoCAD 版本对应的 DLL（命名：`AFR-ACAD20XX.dll`）
3. 存放到固定路径（不要放桌面或临时目录）

> 💡 推荐路径：`D:\CADPlugins\AFR-ACAD20XX.dll`

### 2) 加载插件

1. 打开 AutoCAD
2. 命令行输入 `NETLOAD`
3. 选择对应 DLL 并打开
4. 如有安全提示，选择“始终加载”

> ✅ 成功加载后，插件会在当前配置下写入自动启动项，后续重启 AutoCAD 会自动运行，无需重复使用 `NETLOAD` 操作。

### 3) 首次配置字体

1. 此时插件已自动将内置的默认字体释放到 CAD 的 Fonts 目录。
2. 并在注册表中写入了默认配置：
   - SHX 主字体：`K_roms.shx`
   - SHX 大字体：`tssdchn.shx`
   - TrueType 字体：`宋体`
3. 提示信息显示“首次加载完成，默认替换字体已部署。请重启 CAD 使插件自动生效。”
4. **重新启动 AutoCAD**（必须重启才能使 Hook 拦截模块生效）。

如果需要修改默认配置，可在重启 CAD 后：
1. 命令行输入 `AFR`
2. 在弹出窗口中重新配置三类替换字体（下拉框支持跨类型搜索和关键字搜索）
3. 点击“确认”即可立即生效。

> ⚠️ 字体精简建议：
> - 建议将 CAD 安装目录 `Fonts` 中 SHX 字体精简至 100 个以内
> - 保留 `sas_____.pfb`、`MstnFontConfig.xml`、`internat.rsc`、`font.rsc` 等非 SHX 文件
> - 字体过多会导致插件界面加载明显卡顿
>
> 👉 [点击下载 CAD 字体包（Fonts.zip）](https://github.com/splrad/CADFontAutoReplace/releases)

### 4) 验证效果

打开有缺失字体的 DWG，看到类似日志即说明工作正常：

```
====================================================================================
AFR 缺失字体自动替换 v7.0
项目地址GitHub(国外)：github.com/splrad/CADFontAutoReplace
项目地址Gitee(国内)：gitee.com/splrad/CADFontAutoReplace
命令: AFR(配置) AFRLOG(日志) AFRUNLOAD(卸载)
====================================================================================
[字体修复]已替换缺失字体 3 个(SHX主字体:1,SHX大字体:1,TrueType:1) | MText内联字体映射：1
```

### 5) 手动兜底（可选）

如果自动替换结果不理想：

1. 输入 `AFRLOG`
2. 查看缺失字体与当前替换目标
3. 逐条调整或使用批量填充
4. 点击“应用替换”写入当前图纸

> 💡 `AFRLOG` 每次打开都会重新读取图纸实时状态（含 `STYLE` 修改结果）。
>
> ⚠️ MText 内联字体采用 Hook 重定向自动修复，不支持手动替换。

---

## ⌨️ 命令速查

| 命令 | 说明 |
|:---:|---|
| `AFR` | 打开字体配置界面，选择 SHX 主字体、大字体和 TrueType 替换字体 |
| `AFRLOG` | 打开替换日志，查看检测结果，支持手动调整和批量填充 |
| `AFRUNLOAD` | 完整卸载插件（注销事件、删除注册表项、清空运行状态） |

---

## ❓ 常见问题

<details>
<summary><b>如何修改替换字体配置？</b></summary>

随时输入 `AFR`，重新选择字体并确认即可。新配置会立即对当前图纸生效。

</details>

<details>
<summary><b>替换后文字显示异常怎么办？</b></summary>

**不要保存图纸。** 使用 `AFRLOG` 重新选择替换字体，直至显示正常。

</details>

<details>
<summary><b>如何卸载插件？</b></summary>

输入 `AFRUNLOAD` 后，插件会停止功能并删除自动加载配置。下次启动 AutoCAD 不再加载。

如需恢复，重启 AutoCAD 后再次执行 `NETLOAD`。

</details>

<details>
<summary><b>CAD 打开图纸仍弹出“缺少 SHX 文件”对话框？</b></summary>

本插件不会主动关闭该对话框。建议选择“忽略缺少的 SHX 文件并继续”，并勾选“始终执行我的当前选择”。

</details>

<details>
<summary><b>为什么 AFR 能修复多行文字（MText）乱码？</b></summary>

AFR 在 DWG 解析阶段通过 `ldfile` Hook 拦截字体加载请求。针对 MText 内联字体（`\F` / `\f`）缺失场景，会优先在解析链路中重定向，再由样式表替换阶段统一收敛。

> 注意：若图纸正文字符已经被错误编码保存（文字数据已损坏），字体替换无法恢复原文。

</details>

---

## 🐞 问题反馈

如果使用插件后仍出现“字体不显示”或“乱码”，欢迎提交 [Issues](https://github.com/splrad/CADFontAutoReplace/issues)。

为便于快速定位，请尽量附上：

1. 脱敏后的问题图纸（可最小化为单个问题区域）
2. 同一图纸中“正常显示部分”截图
3. 同一图纸中“不正常显示部分”截图
4. AutoCAD 版本 + 插件 DLL 版本 + 插件配置

信息越完整，定位越快。

---

## 🛠️ 开发者说明

- [新手：开发者指南](docs/developer-guide-beginner.md)
- [进阶：开发者指南](docs/developer-guide-advanced.md)
- [规范：Git 分支模板](docs/git-branch-guidelines.md)

---

## 🤝 贡献者指南

欢迎提交 Issue 和 PR，一起完善 AFR。

### 提交流程

1. 拉取仓库 `main` 最新代码。
2. 从 `main` 切出功能分支并完成修改（本仓库禁止直接向 `main` 分支推送）。
3. 本地构建：

```bash
dotnet build src/AutoCAD/AFR-ACAD20XX/AFR-ACAD20XX.csproj
```

> 将 `20XX` 替换为当前目标版本（例如 `2026`）。

4. 验证关键命令（`AFR` / `AFRLOG` / `AFRUNLOAD`，Debug 下可验证 `AFRVIEW` / `AFRINSERT`）。
5. 推送分支后，流程会自动创建/更新 `你的分支 -> test` 的 PR。
6. `test` 合并后，流程会自动创建/更新 `test -> main` 的 PR。
7. 提交说明与 PR 描述会自动生成；不准确时请在 PR 评论补充。

### 审批与权限规则

- 核心开发者（`TRUSTED_DEVELOPERS`）提交的 PR 会自动审批授权。
- 非核心开发者提交到 `main` 的 PR，需要至少 1 位核心开发者有效审批。
- 非核心开发者向 `test` 提交 PR 时，禁止修改 `.github/workflows/` 下文件。
- `main` 仅允许 `test` 分支发起合并，且禁止外部 Fork 直接向 `main` 提 PR。

### 贡献约定

- 遵守分层依赖方向：`AFR.Core` / `AFR.UI` 不引用 AutoCAD SDK。
- 新增命令必须在 `PluginEntry.cs` 注册，否则 CAD 无法识别。
- 仅调试使用的功能请用 `#if DEBUG` 包裹（并在命令注册处同步控制）。
- 聚焦当前问题，避免无关重构。

---

## 📜 第三方开源库

| 库名 | 版本 | 作者 | 用途 | 许可协议 |
|---|:---:|---|---|:---:|
| [HandyControl](https://github.com/HandyOrg/HandyControl) | 3.5.1 | [NaBian](https://github.com/NaBian) | WPF UI 控件库，提供现代化界面组件 | MIT |

---

## 📜 字体来源声明

本项目提供的 CAD 字体包中，部分字体来自互联网整理。为尊重原作者知识产权，现将可追溯来源列出如下：

| 字体文件 | 来源 / 作者 | 备注 |
|---|---|---|
| [AutoCAD 原版字体清单](docs/autodesk-fonts.md) | Autodesk | 基于 CAD 初始安装释放的 SHX 清单 |
| `tssdchn.shx` `tssdeng.shx` `cadzxw.shx` | 探索者软件 (TSSD) | 探索者结构设计字体 |
| `cadzxw-e.shx` | ChenYong longfly199@sina.com) | 探索者英文字体，基于 ROMANS 修改 |
| `whgdtxt.shx` `whgtxt.shx` `whtgtxt.shx` `whtmtxt.shx` | 天正建筑 | 天正系列中文大字体 |
| `yjkeng.shx` | 盈建科 (YJK) | 基于 TSSD 英文字体修改 |
| `CDM_NC.shx` `Cdm.shx` | CDM 软件 | 工程设计字体 |
| `ming.shx` `ming1.shx` `ming2.shx` | 淘宝店铺：CAD专家 Q421259113 | 基于 tssdeng / Roman Simplex 修改 |

> ⚠️ 若你是某款字体原作者，且认为收录方式不当，请通过 [Issues](https://github.com/splrad/CADFontAutoReplace/issues) 联系，我会第一时间处理（移除或补充署名）。
>
> 本项目字体包仅供学习与辅助使用，不以任何形式进行商业销售。

---

## ☕ 打赏支持

如果本插件对你有帮助，欢迎请开发者喝杯咖啡 ☕

<p align="center">
  <img src="https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260406215922295.jpg" width="560" />
</p>

---

<h2 align="center">⭐ Star History</h2>

<p align="center">
  <a href="https://star-history.com/#splrad/CADFontAutoReplace&Date">
    <img src="https://api.star-history.com/svg?repos=splrad/CADFontAutoReplace&type=Date" />
  </a>
</p>
