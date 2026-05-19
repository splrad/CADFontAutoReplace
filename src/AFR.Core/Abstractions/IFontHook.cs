namespace AFR.Abstractions;

/// <summary>
/// 字体运行时 Hook 的生命周期管理接口。
/// <para>
/// 通过 Hook CAD 字体构造、样式加载或内联 MText 渲染路径，
/// 在运行时将缺失字体临时映射到可用字体，不改写原始文字内容。
/// </para>
/// </summary>
public interface IFontHook
{
    /// <summary>Hook 是否已安装并处于生效状态。</summary>
    bool IsInstalled { get; }
    /// <summary>安装 Hook，开始拦截运行时字体解析请求。</summary>
    void Install();
    /// <summary>卸载 Hook，恢复原始字体解析行为。</summary>
    void Uninstall();
    /// <summary>在 Hook 已安装的情况下，更新映射配置（如替换字体变化时调用）。</summary>
    void UpdateConfig();
}
