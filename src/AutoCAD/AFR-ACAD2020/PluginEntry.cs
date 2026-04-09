using Autodesk.AutoCAD.Runtime;
using AFR.Abstractions;
using AFR.FontMapping;
using AFR.Hosting;

[assembly: ExtensionApplication(typeof(AFR.PluginEntry))]
[assembly: CommandClass(typeof(AFR.Commands.AfrCommands))]
#if DEBUG
[assembly: CommandClass(typeof(AFR.Commands.MTextEditorCommand))]
#endif

namespace AFR;

public class PluginEntry : PluginEntryBase
{
    protected override ICadPlatform CreatePlatform() => new AutoCad2020Platform();
    protected override IFontHook CreateFontHook() => new AutoCadFontHook();
    protected override ICadHost CreateHost() => new AutoCadHost();
}
