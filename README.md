<div align="center">
<h1 style="display:inline-flex;align-items:center;justify-content:center;gap:16px;"><img src="chore/assets/afr-icon.png" alt="AFR 项目图标" width="46" align="absmiddle" style="border-radius:10px;" /> <span>AFR — CAD 缺失字体自动替换工具</span></h1>

**减少缺失字体导致的文字不显示、乱码和反复手动指定字体**

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE.txt)
[![AutoCAD](https://img.shields.io/badge/AutoCAD-2018%E2%80%932027-red.svg)](#-已支持版本)
[![.NET](https://img.shields.io/badge/.NET-4.6.2%20%7C%204.7.2%20%7C%204.8%20%7C%208.0%20%7C%2010.0-purple.svg)](https://dotnet.microsoft.com/)

[GitHub](https://github.com/splrad/CADFontAutoReplace) · [Gitee（国内镜像）](https://gitee.com/splrad/CADFontAutoReplace) · [Releases](https://github.com/splrad/CADFontAutoReplace/releases) · [提交 Issue](https://github.com/splrad/CADFontAutoReplace/issues)

**📥 补充下载链接：[123网盘](https://1819233892.share.123pan.cn/123pan/QtB9jv-OJk7h)**

</div>

---

## ✨ 项目亮点

- **缺失字体自动替换**：AFR 会自动处理图纸中的缺失字体，避免打开图纸因为字体问题造成字符显示不全或乱码。
- **字体缺失场景全覆盖**：同时处理 SHX 主字体、SHX 大字体、TrueType 字体，以及多行文字中的内联字体缺失。
- **安装和卸载无残留**：部署工具/单DLL会自动识别 AutoCAD 2018–2027，完成安装、卸载、字体释放和插件状态检查全流程。
- **支持人工复核**：通过 `AFRLOG` 查看每个缺失字体的替换情况，并对当前图纸逐条或批量调整。

---

## 🚀 快速开始

### 推荐：部署工具一键安装

一般用户只需要下载并运行 **`AFR-Deployer_vX.Y.Z.exe`**；`AFR-DLL_vX.Y.Z.zip` 主要用于维护、测试或受限环境下的手动 `NETLOAD`。

1. 在 [Releases](https://github.com/splrad/CADFontAutoReplace/releases) 下载最新发行包。
2. **先关闭所有 AutoCAD 进程**（部署工具会在检测到 CAD 运行时禁用安装/卸载按钮）。
3. 双击运行 `AFR-Deployer_vX.Y.Z.exe`，工具会自动扫描本机已安装的 AutoCAD 版本。
4. 勾选需要安装的项目，确认“部署路径”（默认会选中首个非系统盘下的 `\CADPlugins\`），点击“安装”。
5. 工具会自动完成：
   - 将对应版本 DLL 复制到部署路径；
   - 在注册表写入自动加载项；
   - 释放内嵌默认 SHX 字体到各 CAD 的 `Fonts` 目录；
   - 写入 `FixedProfile.aws` 以抑制“缺少 SHX 文件”弹窗。
6. 启动 AutoCAD 后，插件自动生效。

> 部署工具会实时监听注册表变化：后续安装/卸载新的 CAD 版本或修改配置文件后，无需手动“刷新”，列表会自动更新。
>
> 卸载同样在部署工具中完成：勾选已安装的项目点击“卸载”，工具会同步还原注册表与 `FixedProfile.aws` 中由本插件写入的节点。

### 单 DLL 手动安装

部署工具是推荐方式；如果只需要单 DLL 场景（例如维护、测试、受限环境），可以手动 `NETLOAD`：

1. 在 [Releases](https://github.com/splrad/CADFontAutoReplace/releases) 下载 `AFR-DLL_vX.Y.Z.zip` 并解压。
2. 按 AutoCAD 版本选择对应 DLL，例如 AutoCAD 2026 使用 `AFR-ACAD2026.dll`。
3. 在 AutoCAD 命令行输入 `NETLOAD`，选择该 DLL。
4. 首次 `NETLOAD` 会完成默认字体部署、配置初始化与自动加载注册；按命令行提示重启 AutoCAD 后生效。

单 DLL 卸载时，在 CAD 命令行完整输入 `AFRUNLOAD`。该命令会卸载当前会话中的插件事件/Hook，并清理 AFR 自动加载注册表项和由插件写入的配置节点。`AFRUNLOAD` 是维护入口，不参与自动补全和动态输入建议，必须完整输入命令名。

### 首次配置与验证

1. 首次安装后启动 AutoCAD，插件会自动将内置默认字体释放到 CAD 的 `Fonts` 目录。
2. 插件会自动写入默认配置：
   - SHX 主字体：`ming.shx`
   - SHX 大字体：`tssdchn.shx`
   - TrueType 字体：`宋体`
3. 如需修改默认配置，可在 AutoCAD 中输入 `AFR`，重新配置三类替换字体。

> 字体精简建议：
> - 建议将 CAD 安装目录 `Fonts` 中 SHX 字体精简至 100 个以内；
> - 保留 `sas_____.pfb`、`MstnFontConfig.xml`、`internat.rsc`、`font.rsc` 等非 SHX 文件；
> - 字体过多会导致插件界面加载明显卡顿。
>
> [点击下载 CAD 字体包（Fonts.zip）](https://github.com/splrad/CADFontAutoReplace/releases)

打开有缺失字体的 DWG，看到类似日志即说明插件已执行：

```text
====================================================================================
AFR 缺失字体自动替换 v9.1.0
项目地址GitHub(国外)：github.com/splrad/CADFontAutoReplace
项目地址Gitee(国内)：gitee.com/splrad/CADFontAutoReplace
命令: AFR(配置) AFRLOG(日志) AFRUNLOAD(卸载命令)
====================================================================================
[字体修复]已替换缺失字体 3 个(SHX主字体:1,SHX大字体:1,TrueType:1) | MText内联字体映射：0
```

### 手动替换

如果自动替换结果不理想：

1. 输入 `AFRLOG`
2. 查看缺失字体与当前替换目标
3. 逐条调整或使用批量填充
4. 点击“应用替换”写入当前图纸

> `AFRLOG` 每次打开都会重新读取图纸实时状态（含 `STYLE` 修改结果）。
>
> MText 内联字体采用运行时映射自动处理，不支持手动替换。

## 🖼️ 界面预览

<table>
<tr>
<td align="center" width="50%" valign="top" style="padding:12px;">
  <img src="https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260407005000713.jpg" alt="AFR 字体配置界面" width="100%" style="display:block;border-radius:16px;background:#ffffff;box-shadow:0 10px 28px rgba(0,0,0,.12);padding:8px;" />
</td>
<td align="center" width="50%" valign="top" style="padding:12px;">
  <img src="https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260407005034079.jpg" alt="AFRLOG 替换日志界面" width="100%" style="display:block;border-radius:16px;background:#ffffff;box-shadow:0 10px 28px rgba(0,0,0,.12);padding:8px;" />
</td>
</tr>
</table>

<table>
<tr>
<td align="center" valign="top" style="padding:12px;">
  <div style="max-width:920px;margin:0 auto;">
    <img src="https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260503193327546.jpg" alt="AFR 部署工具主界面" width="100%" style="display:block;border-radius:16px;background:#ffffff;box-shadow:0 10px 28px rgba(0,0,0,.12);padding:8px;" />
  </div>
</td>
</tr>
</table>

---

## ✅ 已支持版本

| CAD 版本 | DLL 文件名 | .NET |
|:---:|:---:|:---:|
| AutoCAD **2018**（R22.0） | `AFR-ACAD2018.dll` | .NET Framework 4.6.2 |
| AutoCAD **2019**（R23.0） | `AFR-ACAD2019.dll` | .NET Framework 4.7.2 |
| AutoCAD **2020**（R23.1） | `AFR-ACAD2020.dll` | .NET Framework 4.7.2 |
| AutoCAD **2021**（R24.0） | `AFR-ACAD2021.dll` | .NET Framework 4.8 |
| AutoCAD **2022**（R24.1） | `AFR-ACAD2022.dll` | .NET Framework 4.8 |
| AutoCAD **2023**（R24.2） | `AFR-ACAD2023.dll` | .NET Framework 4.8 |
| AutoCAD **2024**（R24.3） | `AFR-ACAD2024.dll` | .NET Framework 4.8 |
| AutoCAD **2025**（R25.0） | `AFR-ACAD2025.dll` | .NET 8.0 |
| AutoCAD **2026**（R25.1） | `AFR-ACAD2026.dll` | .NET 8.0 |
| AutoCAD **2027**（R26.0） | `AFR-ACAD2027.dll` | .NET 10.0 |

---

## ⌨️ 命令说明

| 命令 | 说明 |
|:---:|---|
| `AFR` | 打开字体配置界面，选择 SHX 主字体、大字体和 TrueType 替换字体 |
| `AFRLOG` | 打开替换日志，查看检测结果，支持手动调整和批量填充 |
| `AFRUNLOAD` | 维护用卸载入口；需完整输入，不参与 CAD 自动补全或动态输入建议 |

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

使用 `AFR.Deployer` 执行卸载：勾选已安装项后点击“卸载”，工具会同步清理自动加载配置与由本插件写入的 `FixedProfile.aws` 节点。

单 DLL / `NETLOAD` 场景可在 CAD 命令行完整输入 `AFRUNLOAD` 执行卸载维护。命令不会出现在自动补全列表中，输入 `AFR`、`AFRU` 等前缀不会触发，必须完整输入 `AFRUNLOAD`。

</details>

<details>
<summary><b>为什么 AFR 能处理多行文字（MText）内联字体缺失？</b></summary>

AFR 不改写 `MText.Contents`。当前流程会先修复样式表缺失字体，再通过 `Regen` 让 AutoCAD 原生展开和绘制 MText；内联字体在运行时文件加载阶段映射，`AFRLOG` 中的内联映射记录只来自真实运行时命中结果。

> 注意：若图纸正文字符已经被错误编码保存（文字数据已损坏），字体替换无法恢复原文。

</details>

---

## 🐞 问题反馈

### Issues提交

如果使用插件后仍出现“字体不显示”或“乱码”，欢迎提交 [Issues](https://github.com/splrad/CADFontAutoReplace/issues)。

为便于快速定位，请尽量附上：

1. 脱敏后的问题图纸（可最小化为单个问题区域）
2. 同一图纸中“正常显示部分”截图
3. 同一图纸中“不正常显示部分”截图
4. AutoCAD 版本 + 插件 DLL 版本 + 插件配置

信息越完整，定位越快。

### 联系作者

QQ：1186191934
微信：splrad
电子邮箱：alearner@splrad.com

---

## 🛠️ 开发者说明

- [开发者指南](docs/developer-guide.md)
- [规范：Git 分支模板](docs/git-branch-guidelines.md)

---

## 🤝 贡献者指南

欢迎提交 Issue 和 PR，一起完善 AFR。

### 提交流程

1. 拉取仓库 `main` 最新代码。
2. 从 `main` 切出功能分支并完成修改（本仓库禁止直接向 `main` 分支推送）。
3. 本地构建：

```bash
# 构建某个 CAD 版本适配壳
dotnet build src/AutoCAD/AFR-ACAD20XX/AFR-ACAD20XX.csproj

# 构建部署工具
dotnet build src/AFR.Deployer/AFR.Deployer.csproj

# 一次构建所有项目
dotnet build CADFontAutoReplace.slnx
```

> 将 `20XX` 替换为当前目标版本（例如 `2027`）。插件统一版本号集中在根目录的 `Version.props`（发版时仅修改这个文件）。

4. 验证关键命令（`AFR` / `AFRLOG`，Debug 下可验证 `AFRVIEW`）。
5. 推送分支后，流程会自动创建/更新 `你的分支 -> test` 的 PR。
6. `test` 合并后，流程会自动创建/更新 `test -> main` 的 PR。
7. 提交说明与 PR 描述会自动生成；不准确时请在 PR 评论补充。

### 审批与权限规则

- 核心开发者（`TRUSTED_DEVELOPERS`）提交的 PR 会自动审批授权。
- 非核心开发者提交到 `main` 的 PR，需要至少 1 位核心开发者有效审批。
- 非核心开发者向 `test` 提交 PR 时，禁止修改 `.github/workflows/` 下文件。
- `main` 仅允许 `test` 分支发起合并，且禁止外部 Fork 直接向 `main` 提 PR。

### 贡献约定

- 遵守分层依赖方向：`AFR.Core` / `AFR.UI` 不引用 AutoCAD SDK；`AFR.HostIntegration` 由部署工具与 CAD 插件共用，不反向依赖其中任一方。
- 新增常规命令必须在 `PluginEntry.cs` 注册，否则 CAD 无法识别；隐藏维护入口需避免进入 CAD 命令补全体系。
- 仅调试使用的功能请用 `#if DEBUG` 包裹（并在命令注册处同步控制）。
- 发版需要修改版本号时，仅修改根目录 `Version.props`文件。
- 聚焦当前问题，避免无关重构。

---

## 📜 第三方开源库

| 库名 | 版本 | 作者 | 用途 | 许可协议 |
|---|:---:|---|---|:---:|
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | Microsoft / .NET Community Toolkit | 部署工具的 MVVM 属性通知、命令生成与 ViewModel 基础设施 | MIT |
| [WPF-UI](https://github.com/lepoco/wpfui) | 4.2.1 | [lepoco](https://github.com/lepoco) | 部署工具的 WPF 现代化界面控件与窗口样式 | MIT |
| [System.Management](https://www.nuget.org/packages/System.Management) | 10.0.7 | Microsoft | 部署工具中用于 WMI 进程监听与系统管理查询 | MIT |
| [AutoCAD.NET](https://www.nuget.org/packages/AutoCAD.NET) | 22.0.0 ~ 26.0.0 | Autodesk / 社区封装分发 | 各版本 AutoCAD 托管 API 引用，供插件适配壳编译使用 | 依发布源约定 |
| [System.ValueTuple](https://www.nuget.org/packages/System.ValueTuple) | 4.5.0 | Microsoft | 兼容 AutoCAD 2018 对应的 .NET Framework 4.6.2 目标 | MIT |
| [HandyControl](https://github.com/HandyOrg/HandyControl) | 3.5.1 | [NaBian](https://github.com/NaBian) | 插件侧 WPF UI 控件库，用于字体配置窗口、日志窗口等界面能力 | MIT |

---

## 📜 字体来源声明

本项目提供的 CAD 字体包中，部分字体来自互联网整理。为尊重原作者知识产权，现将可追溯来源列出如下：

| 字体文件 | 来源 / 作者 | 备注 |
|---|---|---|
| [AutoCAD 原版字体清单](docs/autodesk-fonts.md) | Autodesk | 基于 CAD 初始安装释放的 SHX 清单 |
| `tssdchn.shx` `tssdeng.shx` `cadzxw.shx` | 探索者软件 (TSSD) | 探索者结构设计字体 |
| `cadzxw-e.shx` | ChenYong (longfly199@sina.com) | 探索者英文字体，基于 ROMANS 修改 |
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
  <img src="https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260406215922295.jpg" alt="打赏二维码" width="560" />
</p>

---

<h2 align="center">⭐ Star History</h2>

<p align="center">
  <a href="https://star-history.com/#splrad/CADFontAutoReplace&Date">
    <img src="https://api.star-history.com/svg?repos=splrad/CADFontAutoReplace&type=Date" alt="CADFontAutoReplace Star History" />
  </a>
</p>
