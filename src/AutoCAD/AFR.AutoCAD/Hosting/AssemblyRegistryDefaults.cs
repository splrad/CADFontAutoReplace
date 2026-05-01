using AFR.Hosting;
using AFR.Services;

// 声明插件 DLL 部署时应在注册表中预置的默认配置项。
// 部署工具通过 PE 元数据读取这些声明，按声明驱动的方式写入注册表，
// 避免在部署工具中重复定义键名常量造成漂移。
//
// 写入语义（由部署工具实现）：
// - 仅当注册表值不存在时写入，从而保留用户已有的自定义设置。
// - AutoCAD 协议键（LOADER / LOADCTRLS / MANAGED / DESCRIPTION）以及
//   插件版本类标识（PluginVersion / PluginBuildId）由部署工具自身管理，不在此处声明。

[assembly: RegistryDefaultString("MainFont",     EmbeddedFontDeployer.DefaultMainFont)]
[assembly: RegistryDefaultString("BigFont",      EmbeddedFontDeployer.DefaultBigFont)]
[assembly: RegistryDefaultString("TrueTypeFont", EmbeddedFontDeployer.DefaultTrueTypeFont)]
[assembly: RegistryDefaultDword ("IsInitialized",       0)]
[assembly: RegistryDefaultDword ("ConfigSchemaVersion", PluginVersionService.ConfigSchemaVersion)]

// SHX 缺失对话框抑制：在 ProfileSubKey\FixedProfile\General Configuration 下写 FileDialog=1
// 关闭 AutoCAD 2018+ 引入的"缺少 SHX 文件"对话框。
//   * ForceOverwrite：值不存在或不为 1 时写入，把 CAD 默认行为压平成"不弹"。
//   * RemoveOnUninstall：仅当我们实际写过（即 Applications\<AppName>\__Owned 下有标记）时
//     才会在卸载时清除，从而保留用户在安装前的预设以及安装后中途的手动修改。
// 注意：此键名与 AutoCAD 系统变量 FILEDIA 同名但行为不同——本键作用于 SHX 缺失对话框分支，
// 验证机方法已在维护文档中记录。
[assembly: RegistryDefaultDwordAt(
    @"FixedProfile\General Configuration", "FileDialog", 1,
    ForceOverwrite    = true,
    RemoveOnUninstall = true)]
