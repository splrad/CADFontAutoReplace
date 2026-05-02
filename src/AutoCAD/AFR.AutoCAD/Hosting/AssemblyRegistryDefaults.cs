using AFR.Hosting;
using AFR.Services;

// 声明插件 DLL 部署时应在注册表中预置的默认配置项。
// 部署工具（PluginDeployer / PluginUninstaller）与插件 NETLOAD 直装入口
// （ExternalRegistryDefaultsApplier）共用同一份声明，保证两条入口结果一致。
// 升级 DLL 时增删/修改注册表项只需调整下面的 [assembly: ...] 声明，无须改部署工具。
//
// 写入语义：
// - RegistryDefaultString / RegistryDefaultDword：写到 Applications\<AppName> 下，
//   仅当值不存在时写入，从而保留用户已有的自定义设置；卸载时随 Applications\<AppName>
//   子树整体删除，无需单独管理所有权。
// - RegistryDefaultDwordAt：写到 <ProfileSubKey>\<SubPath> 下（典型如 FixedProfile\
//   General Configuration 等 CAD 自身偏好键）。
//     * ForceOverwrite=false（默认）：仅在值缺失时写入，等同上面两个特性的语义。
//     * ForceOverwrite=true：值缺失或现值不等于期望值时覆写；现值已等于期望值时
//       视为"用户预设"放行，不动数据也不打标记。
//     * RemoveOnUninstall=true：实际写入时在 Applications\<AppName>\__Owned\<SubPath>
//       下记录所有权标记；卸载时仅当外部键现值仍等于标记记录的值才删除，
//       从而避免误删用户预设以及安装后中途的手动修改。
//
// AutoCAD 协议键（LOADER / LOADCTRLS / MANAGED / DESCRIPTION）以及插件版本类标识
// （PluginVersion / PluginBuildId）由部署工具自身管理，不在此处声明。

[assembly: RegistryDefaultString("MainFont",     EmbeddedFontDeployer.DefaultMainFont)]
[assembly: RegistryDefaultString("BigFont",      EmbeddedFontDeployer.DefaultBigFont)]
[assembly: RegistryDefaultString("TrueTypeFont", EmbeddedFontDeployer.DefaultTrueTypeFont)]
[assembly: RegistryDefaultDword ("IsInitialized",       0)]
[assembly: RegistryDefaultDword ("ConfigSchemaVersion", PluginVersionService.ConfigSchemaVersion)]

// SHX 缺失对话框抑制不在注册表层做：经实测 AutoCAD 并未把"缺少 SHX 文件"对话框的
// 持久化状态写到注册表（包括 FixedProfile\General Configuration\FileDialog 这类候选）。
// 真实控制点是 %APPDATA%\Autodesk\AutoCAD <year>\R*\<lang>\Support\Profiles\FixedProfile.aws
// 中的 HideableDialog 节点，已迁至 AwsHideableDialogPatcher（共享层）通过 Apply/Cleanup 管理。
