<div align="center">

# AFR — CAD 缺失字体自动替换工具

**打开图纸不再出现文字不显示、乱码，所有缺失字体自动搞定**

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE.txt)
[![AutoCAD](https://img.shields.io/badge/AutoCAD-2026-red.svg)](#已支持的-autocad-版本)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)

[GitHub](https://github.com/splrad/CADFontAutoReplace) · [Gitee（国内镜像）](https://gitee.com/splrad/CADFontAutoReplace) 

</div>

---

## ✨ 为什么选择 AFR

### 🔍 核心亮点速览

- 🚀 **自动化执行**：配置一次后，后续打开图纸自动检测并替换缺失字体。
- 🎯 **三类字体全覆盖**：支持 `SHX 主字体`、`SHX 大字体`、`TrueType` 缺失修复。
- 🧠 **类型安全替换**：自动区分主字体/大字体，避免替换类型错配。
- 🔧 **底层 Hook + 样式表双阶段修复**：兼顾 DWG 解析阶段与样式表持久化修复。
- 🧾 **采用 Hook 方案修复多行文字乱码**：针对 MText 内联字体（`\F` / `\f`）在解析阶段重定向，能有效解决多行文字乱码问题，而不只处理样式表字体。
- 📦 **单 DLL 部署**：第三方 UI 依赖已嵌入，分发简单。
- 🖥️ **可视化人工兜底**：`AFRLOG` 支持逐行与批量修正。
- 🔄 **可回退可卸载**：仅修改当前图纸，不强制保存，支持 `AFRUNLOAD`。

### 🆚 与常见同类插件相比

| 常见做法 | 典型短板 | AFR 的增强点 |
|---|---|---|
| 仅依赖 `FONTALT` 回退 | 对复杂场景覆盖有限，人工干预多 | 自动检测 + 自动替换，减少手工操作 |
| 仅改样式表字体 | 对 MText 内联字体覆盖不足 | 同时处理样式表与 MText 内联字体 |
| 只替换 SHX | TrueType 缺失场景处理不完整 | SHX/大字体/TrueType 三类统一纳管 |
| 只做“替换不验证” | 可能写入不可用字体 | 内置替换后二次验证与统计输出 |
| 纯命令行流程 | 批量调整和复核成本高 | 配置窗口 + 日志窗口 + 手动兜底闭环 |

### 🌟 适合真实工程场景的附加亮点

- 支持 `AFRVIEW` / `AFRINSERT`（Debug）进行内联字体问题复现与定位。
- 诊断日志可追踪执行阶段、Hook 重定向与替换结果，便于问题排查。

### 界面预览

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

### 已支持的 AutoCAD 版本

| DLL 文件名 | AutoCAD 版本 | .NET |
|:---:|:---:|:---:|
| `AFR-ACAD2026.dll` | AutoCAD **2026**（R25.1） | .NET 8 |

### 开发计划

| 平台 | 版本 | 状态 |
|---|---|---|
| AutoCAD | 2026 | ✅ **已支持** |
| AutoCAD | 2025 | ⬜ 计划中 |
| AutoCAD | 2024 | ⬜ 计划中 |
| AutoCAD | 2023 | ⬜ 计划中 |
| AutoCAD | 2022 | ⬜ 计划中 |
| 中望CAD | 2026 | ⬜ 计划中 |
| 中望CAD | 2025 | ⬜ 计划中 |

---

## 📖 使用指南

> 以下教程面向**从未使用过 AutoCAD 插件**的新手用户，老手可直接跳到[命令速查](#命令速查)。

### 第一步：下载插件

1. 前往 [Releases](https://github.com/splrad/CADFontAutoReplace/releases) 页面
2. 根据你的 AutoCAD 版本下载对应的 DLL 文件（命名格式：`AFR-ACAD20XX.dll`）
3. 保存到一个**固定位置**，不要放在桌面或临时文件夹

> 💡 推荐路径：`D:\CADPlugins\AFR-ACAD20XX.dll`

### 第二步：加载插件

1. 打开 AutoCAD
2. 在底部命令行输入 `NETLOAD`，按回车
3. 在弹出的文件选择窗口中，找到对应版本的 DLL（如 `AFR-ACAD20XX.dll`），点击"打开"
4. 如果弹出安全警告，选择 **"始终加载"**

> ✅ 加载成功后，命令行会显示插件信息。此后每次打开 AutoCAD 都会**自动加载**，无需重复操作。

> ⚠️ **字体精简建议：强烈建议将 CAD 安装目录下 `Fonts` 文件夹中的 SHX 字体精简至 100 个以内（保留 `sas_____.pfb`、`MstnFontConfig.xml`、`internat.rsc`、`font.rsc` 等非 SHX 文件）。字体数量过多会导致插件加载UI界面时明显卡顿。 建议清理后将本项目提供的 CAD 字体包解压拷贝进去，该字体包在 CAD 原版基础上仅增加了几款常用通用字体（如探索者等），可放心使用**。
>
> 👉 **[点击下载 CAD 字体包（Fonts.zip）](https://github.com/splrad/CADFontAutoReplace/releases)**

### 第三步：首次配置替换字体

1. 命令行输入 `AFR`，按回车
2. 在弹出的字体选择窗口中配置三种替换字体：

   | 字体类型 | 说明 | 推荐选择 |
   |:---:|---|---|
   | **SHX 主字体（西文）** | 替换所有缺失的 SHX 字体 | `ming.shx` |
   | **SHX 大字体（中文）** | 替换缺失的中文大字体 | `hztxt.shx` |
   | **TrueType 字体** | 替换缺失的 TrueType 字体 | `宋体` |

   > 下拉框支持直接输入搜索，比如输入 `txt` 就会筛选出 `txt.shx`

3. 点击 **确认**
4. **重启 CAD**（首次配置需要重启以安装 Hook 模块，此后无需再重启）

> ✅ 配置完成！此后打开任何图纸，插件都会自动替换缺失字体。

### 第四步：验证效果

打开一张有缺失字体的 DWG 文件，观察命令行输出：

```
====================================================================================
AFR 缺失字体自动替换 v7.0
项目地址GitHub(国外)：github.com/splrad/CADFontAutoReplace
项目地址Gitee(国内)：gitee.com/splrad/CADFontAutoReplace
命令: AFR(配置) AFRLOG(日志) AFRUNLOAD(卸载)
====================================================================================
[字体修复]已替换缺失字体 3 个(SHX主字体:1,SHX大字体:1,TrueType:1) | MText内联字体映射：1
```

看到类似的替换记录，说明插件正在正常工作 🎉

### 第五步：手动调整（可选）

如果自动替换的字体不理想，可以手动调整：

1. 命令行输入 `AFRLOG`，按回车
2. 查看每个缺失字体的当前替换状态
3. 逐行修改替换字体，或使用底部的**批量填充**功能
4. 点击 **应用替换** 写入当前图纸

> 💡 `AFRLOG` 每次打开都会重新读取图纸中的实际字体状态，即使通过 `STYLE`（样式管理器）命令修改过也会正确显示。

> ⚠ MText 内联字体（多行文本中的嵌入字体）采用 Hook 重定向方案自动修复，不支持手动替换。

### 常见问题

<details>
<summary><b>如何修改替换字体配置？</b></summary>

随时输入 `AFR` 命令，重新选择字体并确认即可。新配置会立即对当前图纸生效。

</details>

<details>
<summary><b>替换后文字显示异常怎么办？</b></summary>

**不要保存图纸！** 使用 `AFRLOG` 命令打开日志窗口，选择其他字体进行替换，直到文字显示正常。

</details>

<details>
<summary><b>如何卸载插件？</b></summary>

命令行输入 `AFRUNLOAD`，插件会自动停止所有功能并删除注册表中的自动加载配置。下次启动 AutoCAD 将不再加载。

如需重新安装，重启 AutoCAD 后再次 `NETLOAD` 即可。

</details>

<details>
<summary><b>CAD 打开图纸时还是弹出"缺少 SHX 文件"对话框？</b></summary>

本插件不会主动关闭该对话框。建议在弹出时选择 **"忽略缺少的 SHX 文件并继续"**，并勾选 **"始终执行我的当前选择"**。之后插件会自动完成字体替换。

</details>

<details>
<summary><b>为什么 AFR 能修复多行文字（MText）乱码？</b></summary>

AFR 采用了底层 `ldfile` Hook 方案，在 DWG 解析阶段拦截字体加载请求。对 MText 内联字体（`\F` / `\f`）缺失场景，
会在解析链路中完成重定向，再由样式表替换阶段做统一收敛，因此不仅能修复普通样式表缺失，也能覆盖多行文字乱码问题。

> 注意：若图纸内容字符本身已经被错误编码后保存（文字数据已损坏），任何字体替换都无法还原原文。

</details>

### 命令速查

| 命令 | 说明 |
|:---:|---|
| `AFR` | 打开字体配置界面，选择 SHX 主字体、大字体和 TrueType 替换字体 |
| `AFRLOG` | 打开替换日志，查看检测结果，支持手动调整和批量填充 |
| `AFRUNLOAD` | 完整卸载插件 — 注销事件、删除注册表项、清空运行状态 |

---

## 🛠️ 开发者说明

开发相关内容已拆分到独立文档：

- [开发者指南入口](docs/developer-guide.md)
- [纯新手：开发者指南](docs/developer-guide-beginner.md)
- [老手进阶：开发者指南](docs/developer-guide-advanced.md)

`README.md` 仅保留项目简介与用户使用说明。

---

## 🤝 贡献者指南（简短版）

欢迎提交 Issue 和 PR，一起完善 AFR。

### 提交流程

1. 从 `test` 分支拉取最新代码并完成你的修改。
2. 完成修改后本地执行构建：

```bash
dotnet build src/AutoCAD/AFR-ACAD20XX/AFR-ACAD20XX.csproj
```

> 说明：请将 `20XX` 替换为你当前要开发/验证的版本壳工程（例如 `2026`）。

3. 确认关键命令可用（`AFR` / `AFRLOG` / `AFRUNLOAD`，Debug 下可验证 `AFRVIEW` / `AFRINSERT`）。
4. 将代码推送到仓库后，PR 会自动拉取到 `test` 分支。
5. 提交说明与 PR 描述由流程自动生成，无需手动编写。

> 如自动生成的说明与实际改动不一致，请在 PR 评论区补充变更摘要（修改目的、影响范围、验证方式）。

### 贡献约定

- 遵守分层依赖方向：`AFR.Core` / `AFR.UI` 不引用 AutoCAD SDK。
- 新增命令必须在 `PluginEntry.cs` 注册，否则 CAD 无法识别。
- 仅调试使用的功能请使用 `#if DEBUG` 包裹（并在命令注册处同步控制）。
- 仅修复当前问题，避免无关重构。

---

## 📜 第三方开源库

本项目使用了以下开源库，感谢各位作者的贡献：

| 库名 | 版本 | 作者 | 用途 | 许可协议 |
|---|:---:|---|---|:---:|
| [HandyControl](https://github.com/HandyOrg/HandyControl) | 3.5.1 | [NaBian](https://github.com/NaBian) | WPF UI 控件库，提供现代化界面组件 | MIT |

---

## 📜 字体来源声明

本项目提供的 CAD 字体包中，部分字体从互联网收集整理。为尊重原作者的知识产权，现将字体包中所有字体的来源逐一列出，部分字体来源已无法追溯：

| 字体文件 | 来源 / 作者 | 备注 |
|---|---|---|
| [AutoCAD 原版字体清单](docs/autodesk-fonts.md) | Autodesk | 基于 CAD 初始安装释放的 SHX 清单 |
| `tssdchn.shx` `tssdeng.shx` `cadzxw.shx` | 探索者软件 (TSSD) | 探索者结构设计字体 |
| `cadzxw-e.shx` | ChenYong longfly199@sina.com) | 探索者英文字体，基于 ROMANS 修改 |
| `whgdtxt.shx` `whgtxt.shx` `whtgtxt.shx` `whtmtxt.shx` | 天正建筑 | 天正系列中文大字体 |
| `yjkeng.shx` | 盈建科 (YJK) | 基于 TSSD 英文字体修改 |
| `CDM_NC.shx` `Cdm.shx` | CDM 软件 | 工程设计字体 |
| `ming.shx` `ming1.shx` `ming2.shx` | 淘宝店铺：CAD专家 Q421259113 | 基于 tssdeng / Roman Simplex 修改 |

> ⚠️ 部分字体来源已无法追溯。如果你是某款字体的原作者，且认为本项目的收录方式不当，请通过 [Issues](https://github.com/splrad/CADFontAutoReplace/issues) 联系我，我会**立即处理**（移除或补充署名）。
>
> 本项目字体包仅供学习与辅助使用，不以任何形式进行商业销售。

---

## ☕ 打赏支持

如果本插件对你有帮助，欢迎请开发者喝杯咖啡 ☕ 你的支持是持续开发的动力！

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