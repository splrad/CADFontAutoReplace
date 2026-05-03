using AFR.Deployer.Models;
using AFR.HostIntegration;

namespace AFR.Deployer.Services;

/// <summary>
/// 部署器侧 "缺少 SHX 文件" 对话框抑制器（薄包装：从 <see cref="CadDescriptor"/> 取 CAD 元数据，
/// 委托给 <see cref="AwsHideableDialogPatcherCore"/> 执行实际算法）。
/// <para>
/// 与插件侧 <c>AFR.Diagnostics.AwsHideableDialogPatcher</c> 共用同一份核心算法，差异仅在数据来源：
/// 部署器在 CAD 已关闭后由用户交互触发，所有 CAD 元数据由 <see cref="CadDescriptor"/> 提供，
/// 不依赖 <c>PlatformManager</c>。
/// </para>
/// </summary>
internal static class AwsHideableDialogPatcher
{
    /// <summary>对指定 CAD 版本的所有 <c>FixedProfile.aws</c> 写入抑制节点。</summary>
    /// <returns>实际写入或刷新的文件数量。</returns>
    public static int Apply(CadDescriptor descriptor)
        => AwsHideableDialogPatcherCore.Apply(descriptor.Brand, descriptor.Version, descriptor.RegistryBasePath);

    /// <summary>删除指定 CAD 版本对应 <c>FixedProfile.aws</c> 中带 AFR 所有权标记的抑制节点。</summary>
    /// <returns>实际清理的文件数量。</returns>
    public static int Cleanup(CadDescriptor descriptor)
        => AwsHideableDialogPatcherCore.Cleanup(descriptor.Brand, descriptor.Version, descriptor.RegistryBasePath);
}
