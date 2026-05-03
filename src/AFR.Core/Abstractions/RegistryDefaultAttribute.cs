namespace AFR.Hosting;

/// <summary>
/// 声明插件首次部署时应写入注册表的字符串类型默认值。
/// <para>
/// 由部署工具通过 PE 元数据读取并循环写入；插件端可使用相同特性自我描述配置 schema，
/// 修改/新增/删除非协议键时只需调整 <c>[assembly: RegistryDefaultString(...)]</c> 声明，
/// 部署工具自动同步，无需重复定义键名或常量。
/// </para>
/// <para>
/// 写入语义：仅当注册表中尚不存在该值名时写入，避免覆盖用户已有的自定义设置。
/// </para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class RegistryDefaultStringAttribute : System.Attribute
{
    /// <summary>注册表值名。</summary>
    public string Name { get; }

    /// <summary>注册表字符串值。</summary>
    public string Value { get; }

    /// <summary>声明一项字符串默认值。</summary>
    /// <param name="name">注册表值名。</param>
    /// <param name="value">注册表字符串值。</param>
    public RegistryDefaultStringAttribute(string name, string value)
    {
        Name = name;
        Value = value;
    }
}

/// <summary>
/// 声明插件首次部署时应写入注册表的 DWORD 类型默认值。
/// <para>写入语义同 <see cref="RegistryDefaultStringAttribute"/>。</para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class RegistryDefaultDwordAttribute : System.Attribute
{
    /// <summary>注册表值名。</summary>
    public string Name { get; }

    /// <summary>注册表 DWORD 值。</summary>
    public int Value { get; }

    /// <summary>声明一项 DWORD 默认值。</summary>
    /// <param name="name">注册表值名。</param>
    /// <param name="value">注册表 DWORD 值。</param>
    public RegistryDefaultDwordAttribute(string name, int value)
    {
        Name = name;
        Value = value;
    }
}

/// <summary>
/// 声明插件部署时应写入"配置文件子键的任意相对子路径"下的 DWORD 默认值。
/// <para>
/// 与 <see cref="RegistryDefaultDwordAttribute"/> 的差异：
/// <list type="bullet">
///   <item><description>写入位置不是 <c>Applications\&lt;AppName&gt;</c>，而是 <c>&lt;ProfileSubKey&gt;\&lt;SubPath&gt;</c>，
///         典型用例如 <c>FixedProfile\General Configuration</c> 等 CAD 自身偏好键。</description></item>
///   <item><description><see cref="ForceOverwrite"/> = true 时，若现值与期望值不同则强制覆盖；
///         默认 false 时与现行 <c>仅在不存在时写入</c> 语义一致。</description></item>
///   <item><description><see cref="RemoveOnUninstall"/> = true 时，部署工具在 <c>Applications\&lt;AppName&gt;\__Owned</c>
///         下记录所有权标记，仅当卸载时该值仍为我们写入的内容才删除——避免误删用户预设或中途修改后的值。</description></item>
/// </list>
/// </para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class RegistryDefaultDwordAtAttribute : System.Attribute
{
    /// <summary>相对于配置文件子键（<c>ProfileSubKey</c>）的子路径，例如 <c>FixedProfile\General Configuration</c>。</summary>
    public string SubPath { get; }

    /// <summary>注册表值名。</summary>
    public string Name { get; }

    /// <summary>期望写入的 DWORD 值。</summary>
    public int Value { get; }

    /// <summary>是否在现值与期望值不同时强制覆盖。</summary>
    public bool ForceOverwrite { get; set; }

    /// <summary>是否在卸载时按所有权标记驱动清理。</summary>
    public bool RemoveOnUninstall { get; set; }

    /// <summary>声明一项位于任意子路径下的 DWORD 默认值。</summary>
    /// <param name="subPath">相对于配置文件子键的子路径。</param>
    /// <param name="name">注册表值名。</param>
    /// <param name="value">期望写入的 DWORD 值。</param>
    public RegistryDefaultDwordAtAttribute(string subPath, string name, int value)
    {
        SubPath = subPath;
        Name = name;
        Value = value;
    }
}
