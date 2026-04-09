<div align="center">

# AFR — CAD 缺失字体自动替换工具

**打开图纸不再弹出"缺少 SHX 文件"，所有缺失字体自动搞定**

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE.txt)
[![AutoCAD](https://img.shields.io/badge/AutoCAD-2026-red.svg)](#已支持的-autocad-版本)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)

[GitHub](https://github.com/splrad/CADFontAutoReplace) · [Gitee（国内镜像）](https://gitee.com/splrad/CADFontAutoReplace) · [下载最新版](https://github.com/splrad/CADFontAutoReplace/releases)

</div>

---

## ✨ 为什么选择 AFR

<table>
<tr>
<td width="50%">

### 🚀 全自动，零操作
打开图纸即自动检测并替换所有缺失字体，无需手动干预。
首次配置好替换字体后，以后打开任何图纸都不再需要操心字体问题。

### 🎯 三种字体全覆盖
- **SHX 主字体**（西文） — 如 `txt.shx`、`simplex.shx`
- **SHX 大字体**（中文） — 如 `hztxt.shx`、`gbcbig.shx`
- **TrueType 字体** — 如 `宋体`、`黑体`

插件会自动读取 SHX 文件头区分主字体与大字体，
配置时**不会选错类型**，无需人工判断。

### 🔧 底层 Hook 修复多行文本
通过 Inline Hook 拦截 AutoCAD 底层字体加载函数，
在 DWG 解析阶段就完成 MText（多行文本）内联字体的重定向，
确保多行文本的内嵌字体也能正确显示。

</td>
<td width="50%">

### 📦 单文件分发
只需一个 DLL 文件，无需额外依赖。
第三方库（HandyControl）已嵌入程序集资源。

### 🖥️ 现代化界面
采用 WPF + HandyControl 构建，提供美观的配置窗口和替换日志界面。

### 📋 详细日志 + 手动调整
`AFRLOG` 命令可查看所有缺失字体的检测结果，
支持逐行修改或按类型批量填充替换字体。

### 🔄 安全可靠
- 替换后二次验证，确认替换字体确实可用
- 仅修改当前图纸内存中的字体引用，不自动保存文件
- 提供 `AFRUNLOAD` 一键完整卸载

</td>
</tr>
</table>

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

**AutoCAD**
- [x] AutoCAD 2026
- [ ] AutoCAD 2025
- [ ] AutoCAD 2024
- [ ] AutoCAD 2023
- [ ] AutoCAD 2022

**中望CAD**
- [ ] 中望CAD 2026
- [ ] 中望CAD 2025

---

## 📖 使用指南

> 以下教程面向**从未使用过 AutoCAD 插件**的新手用户，老手可直接跳到[命令速查](#命令速查)。

### 第一步：下载插件

1. 前往 [Releases](https://github.com/splrad/CADFontAutoReplace/releases) 页面
2. 根据你的 AutoCAD 版本下载对应的 DLL 文件（例如 AutoCAD 2026 → `AFR-ACAD2026.dll`）
3. 保存到一个**固定位置**，不要放在桌面或临时文件夹

> 💡 推荐路径：`D:\CADPlugins\AFR-ACAD2026.dll`

### 第二步：加载插件

1. 打开 AutoCAD
2. 在底部命令行输入 `NETLOAD`，按回车
3. 在弹出的文件选择窗口中，找到 `AFR-ACAD2026.dll`，点击"打开"
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

### 命令速查

| 命令 | 说明 |
|:---:|---|
| `AFR` | 打开字体配置界面，选择 SHX 主字体、大字体和 TrueType 替换字体 |
| `AFRLOG` | 打开替换日志，查看检测结果，支持手动调整和批量填充 |
| `AFRUNLOAD` | 完整卸载插件 — 注销事件、删除注册表项、清空运行状态 |

---

## 🛠️ 开发者指南

### 环境要求

- Visual Studio 2022 或更高版本
- .NET 8 SDK
- AutoCAD 2026（运行时测试）

### 构建

```bash
# 打开解决方案后，构建 AFR-ACAD2026 项目
# 所有 Shared Project 源码会自动编译进最终的 DLL
dotnet build src/AutoCAD/AFR-ACAD2026/AFR-ACAD2026.csproj
```

### 项目结构

```
src/
├── AFR.Core/              Shared Project — 接口、模型、基础服务（纯 .NET，无 CAD 依赖）
│   ├── Abstractions/        接口定义（ICadPlatform / IFontHook / ICadHost / IFontScanner / ILogService）
│   ├── Models/              数据模型（FontCheckResult / InlineFontFixRecord / StyleFontReplacement）
│   ├── Platform/            平台管理器（PlatformManager — 全局服务注册中心）
│   └── Services/            基础服务（ConfigService / RegistryService — 注册表配置读写）
├── AFR.UI/                Shared Project — WPF 用户界面（字体选择 / 替换日志 / MText 查看器）
│   ├── FontSelection/       AFR 命令的字体配置窗口
│   ├── FontLog/             AFRLOG 命令的替换日志窗口（支持逐行和批量替换）
│   └── MTextEditor/         AFRVIEW 命令的 MText 格式代码查看器（仅 Debug）
└── AutoCAD/
    ├── AFR.AutoCAD/        Shared Project — AutoCAD 通用逻辑
    │   ├── Commands/          命令定义（AFR / AFRLOG / AFRUNLOAD / AFRVIEW）
    │   ├── FontMapping/       字体 Hook 与 MText 内联字体解析
    │   ├── Hosting/           插件生命周期、事件注册、执行控制
    │   └── Services/          字体检测、替换、日志、诊断
    └── AFR-ACAD2026/       版本适配壳 — 仅 PluginEntry + 平台常量（2 个文件）
```

> AFR.Core、AFR.UI、AFR.AutoCAD 均为 Shared Project，所有源码在编译时直接嵌入最终的 `AFR-ACAD2026.dll`，实现**单 DLL 分发**。

### 添加新 AutoCAD 版本支持

1. 在 `src/AutoCAD/` 下创建新的版本目录（如 `AFR-ACAD2025/`）
2. 创建 `PluginEntry.cs` — 继承 `PluginEntryBase`，实现三个工厂方法
3. 创建 `AutoCad2025Platform.cs` — 实现 `ICadPlatform`，填入版本特定常量（注册表路径、DLL 名、导出符号、序言长度）
4. 创建 `.csproj`，导入三个 Shared Project 的 `.projitems`

### 技术架构

#### 两阶段字体修复

插件采用两阶段协作策略，覆盖从 DWG 解析到样式表修改的完整流程：

```
┌─ 阶段 1：DWG 解析阶段（ldfile Hook） ──────────────────────────────┐
│  拦截 AutoCAD 底层的字体文件加载函数                                  │
│  ├─ 缺失 SHX 大字体（param2=4）→ 重定向到配置的 BigFont              │
│  ├─ 缺失 TrueType 字族名       → 重定向到配置的 TrueType 字体        │
│  └─ 缺失 SHX 主字体（param2=0）→ 放行给 FONTALT 原生机制处理         │
└────────────────────────────────────────────────────────────────────┘
                                ↓
┌─ 阶段 2：Execute 阶段（FontReplacer） ─────────────────────────────┐
│  修改图纸 TextStyleTable 中的字体引用                                │
│  ├─ 检测缺失字体（FontDetector）                                    │
│  ├─ 预校验替换字体可用性                                             │
│  ├─ 按类型分流替换（SHX 主字体 / 大字体 / TrueType）                 │
│  ├─ 清理 TrueType 可用但 SHX 缺失的残留引用                         │
│  └─ 二次验证：替换后重新检测，确认修复效果                             │
└────────────────────────────────────────────────────────────────────┘
```

#### 插件生命周期

```
AutoCAD 启动
  │
  ├─ PluginEntryBase.Initialize()
  │   ├─ 注册平台服务（PlatformManager）
  │   ├─ 安装 ldfile Hook（LdFileHook.Install）
  │   ├─ 写入注册表自动加载项
  │   └─ 注册 DocumentOpened 事件
  │
  ├─ 文档打开事件
  │   ├─ 配置未初始化 → 延迟入队，等待用户执行 AFR 命令后统一处理
  │   └─ 配置已初始化 → ExecutionController.Execute()
  │       ├─ 检测缺失字体
  │       ├─ 替换缺失字体
  │       ├─ 清理残留引用
  │       ├─ MText 内联字体扫描与比对
  │       ├─ 二次验证
  │       └─ 输出日志到命令行
  │
  └─ AFRUNLOAD 命令
      ├─ 注销所有事件监听
      ├─ 卸载 ldfile Hook
      ├─ 删除注册表自动加载项
      └─ 清空运行状态
```

#### 关键设计决策

| 决策 | 原因 |
|---|---|
| TrueType 必须用 TrueType 替换，不能用 SHX | 若将 TrueType 误重定向为 SHX，会污染 AutoCAD 内部字体缓存，导致文字乱码 + ST 弹窗 |
| 常规 SHX 主字体（param2=0）不通过 Hook 重定向 | Hook 级别的重定向会干扰块参照的字体缓存渲染，交由 FONTALT 原生机制处理更稳定 |
| SHX 大字体（param2=4）必须通过 Hook 处理 | FONTALT 不区分大字体和主字体，无法正确替换大字体 |
| 原生字符串指针缓存不释放 | ldfile 可能将 fileName 指针存入全局字体表，释放后成为悬空指针导致崩溃 |
| FontDetectionContext 按事务隔离 | 不同图纸、不同执行次数之间 100% 内存隔离，避免缓存污染 |
| ShapeFile 样式始终跳过 | 替换 ShapeFile 样式会破坏复杂线型结构（ltypeshp.shx 等） |

### 调试日志

Debug 构建会在插件 DLL 所在目录生成 `AFR_Diag_*.log` 诊断日志，记录完整的字体检测、替换、Hook 重定向过程。日志自动按 10MB 分包，保留 7 天。

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
| [AutoCAD 原版字体](docs/autodesk-fonts.md) | Autodesk | 点击查看完整清单 |
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
  <img src="https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260406215922295.jpg" width="460" />
</p>

---

<h2 align="center">⭐ Star History</h2>

<p align="center">
  <a href="https://star-history.com/#splrad/CADFontAutoReplace&Date">
    <img src="https://api.star-history.com/svg?repos=splrad/CADFontAutoReplace&type=Date" />
  </a>
</p>