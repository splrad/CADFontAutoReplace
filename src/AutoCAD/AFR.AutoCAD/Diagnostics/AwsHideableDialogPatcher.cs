using AFR.Platform;
using AFR.Services;
using AFR.HostIntegration;

namespace AFR.Diagnostics;

/// <summary>
/// 插件侧 "缺少 SHX 文件" 对话框抑制器（薄包装：从 <see cref="PlatformManager"/> 取 CAD 元数据，
/// 委托给 <see cref="AwsHideableDialogPatcherCore"/> 执行实际算法）。
/// <para>
/// 详细行为见 <see cref="AwsHideableDialogPatcherCore"/>；本类仅提供：
/// <list type="bullet">
///   <item>从运行时 <c>ICadPlatform</c> 解析 brand / version / registryBasePath；</item>
///   <item>将诊断输出转接到 <c>DiagnosticLogger</c>。</item>
/// </list>
/// </para>
/// </summary>
public static class AwsHideableDialogPatcher
{
    private const string Brand = "AutoCAD";

    /// <summary>对当前插件版本对应的所有 .aws 文件写入抑制节点。</summary>
    public static int Apply()
        => AwsHideableDialogPatcherCore.Apply(Brand, GetVersion(), GetRegistry(), Log);

    /// <summary>删除当前插件版本对应的所有 .aws 文件中带 AFR 所有权标记的抑制节点。</summary>
    public static int Cleanup()
        => AwsHideableDialogPatcherCore.Cleanup(Brand, GetVersion(), GetRegistry(), Log);

    /// <summary>枚举本插件版本对应的所有 <c>FixedProfile.aws</c> 候选路径（诊断用）。</summary>
    public static string[] ListTargetAwsFiles()
        => AwsHideableDialogPatcherCore.ListTargetAwsFiles(Brand, GetVersion(), GetRegistry());

    /// <summary>定位活动 <c>FixedProfile.aws</c>：候选中最近修改的一个；无候选返回 null。</summary>
    public static string? LocateActiveAwsPath()
        => AwsHideableDialogPatcherCore.LocateActiveAwsPath(Brand, GetVersion(), GetRegistry());

    /// <summary>读取指定 .aws 中 HideableDialog[id=Acad.UnresolvedFontFiles] 节点 XML。</summary>
    public static string ReadDialogNodeXml(string awsPath)
        => AwsHideableDialogPatcherCore.ReadDialogNodeXml(awsPath);

    private static string GetVersion()    => PlatformManager.Platform.VersionName;
    private static string GetRegistry()   => PlatformManager.Platform.RegistryBasePath;
    private static void   Log(string tag, string message) => DiagnosticLogger.Log(tag, message);
}
