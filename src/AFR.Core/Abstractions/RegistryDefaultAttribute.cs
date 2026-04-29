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
