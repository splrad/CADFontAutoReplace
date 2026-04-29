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
