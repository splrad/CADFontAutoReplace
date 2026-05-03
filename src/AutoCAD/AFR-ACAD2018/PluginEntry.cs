using Autodesk.AutoCAD.Runtime;
using AFR.Abstractions;
using AFR.FontMapping;
using AFR.Hosting;

[assembly: ExtensionApplication(typeof(AFR.PluginEntry))]
[assembly: CommandClass(typeof(AFR.Commands.AfrCommands))]

namespace AFR;

/// <summary>
/// AutoCAD 2018 版本的插件入口点。
/// 继承 <see cref="PluginEntryBase"/>，提供 2018 版本特定的平台常量、字体 Hook 和宿主实现。
/// </summary>
public class PluginEntry : PluginEntryBase
{
    /// <inheritdoc />
    protected override ICadPlatform CreatePlatform() => new AutoCad2018Platform();
    /// <inheritdoc />
    protected override IFontHook CreateFontHook() => new AutoCadFontHook();
    /// <inheritdoc />
    protected override ICadHost CreateHost() => new AutoCadHost();
}
