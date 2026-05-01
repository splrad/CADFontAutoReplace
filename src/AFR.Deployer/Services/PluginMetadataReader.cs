using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AFR.Deployer.Services;

/// <summary>
/// 通过 <see cref="MetadataReader"/> 直接读取插件 DLL 的 PE 元数据，提取部署所需信息：
/// <list type="bullet">
/// <item>插件版本与构建标识（来自 <c>AssemblyInformationalVersion</c>）。</item>
/// <item>注册表默认值清单（来自 DLL 中声明的 <c>RegistryDefaultStringAttribute</c> /
///       <c>RegistryDefaultDwordAttribute</c> 程序集级特性）。</item>
/// </list>
/// <para>
/// 部署工具不再硬编码非协议键的键名与默认值，而是读取 DLL 自我描述的 schema。
/// 升级 DLL 时增删/修改注册表项只需调整插件侧的 <c>[assembly: ...]</c> 声明，部署工具自动跟随。
/// </para>
/// <para>
/// 使用 <see cref="MetadataReader"/> 而非 <c>MetadataLoadContext</c>：仅解析元数据流本身，
/// 不会触发对引用程序集（如 AutoCAD 的 <c>Acdbmgd</c>）的解析，
/// 因此可安全处理任意目标 CAD 版本的插件 DLL。
/// </para>
/// </summary>
internal static class PluginMetadataReader
{
    private const string InformationalVersionAttr =
        "System.Reflection.AssemblyInformationalVersionAttribute";

    private const string RegistryDefaultStringAttr =
        "AFR.Hosting.RegistryDefaultStringAttribute";

    private const string RegistryDefaultDwordAttr =
        "AFR.Hosting.RegistryDefaultDwordAttribute";

    private const string RegistryDefaultDwordAtAttr =
        "AFR.Hosting.RegistryDefaultDwordAtAttribute";

    /// <summary>声明的注册表默认值类型。</summary>
    internal enum RegistryDefaultKind
    {
        /// <summary>字符串值（REG_SZ）。</summary>
        String,
        /// <summary>32 位整型值（REG_DWORD）。</summary>
        Dword,
        /// <summary>位于配置文件子键下任意子路径的 32 位整型值（REG_DWORD）。</summary>
        DwordAt
    }

    /// <summary>
    /// 一条来自 DLL 声明的注册表默认值。
    /// <para>
    /// 写入位置：
    /// <list type="bullet">
    ///   <item><description><see cref="RegistryDefaultKind.String"/> / <see cref="RegistryDefaultKind.Dword"/>：
    ///         写到 <c>Applications\&lt;AppName&gt;</c> 下，仅当 <see cref="Name"/> 不存在时写入。</description></item>
    ///   <item><description><see cref="RegistryDefaultKind.DwordAt"/>：写到
    ///         <c>&lt;ProfileSubKey&gt;\&lt;SubPath&gt;</c> 下；当 <see cref="ForceOverwrite"/> = true
    ///         时若现值与期望值不同则覆盖；当 <see cref="RemoveOnUninstall"/> = true 时由部署工具记录所有权标记
    ///         以驱动卸载清理。</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="Name">注册表值名。</param>
    /// <param name="Kind">值类型。</param>
    /// <param name="StringValue">类型为 <see cref="RegistryDefaultKind.String"/> 时的值，否则为 null。</param>
    /// <param name="DwordValue">类型为 <see cref="RegistryDefaultKind.Dword"/> / <see cref="RegistryDefaultKind.DwordAt"/> 时的值。</param>
    /// <param name="SubPath">仅 <see cref="RegistryDefaultKind.DwordAt"/> 使用：相对于配置文件子键的子路径。</param>
    /// <param name="ForceOverwrite">仅 <see cref="RegistryDefaultKind.DwordAt"/> 使用：是否强制覆写。</param>
    /// <param name="RemoveOnUninstall">仅 <see cref="RegistryDefaultKind.DwordAt"/> 使用：是否在卸载时按所有权标记驱动清理。</param>
    internal sealed record RegistryDefault(
        string Name,
        RegistryDefaultKind Kind,
        string? StringValue,
        int DwordValue,
        string? SubPath = null,
        bool ForceOverwrite = false,
        bool RemoveOnUninstall = false);

    /// <summary>从插件 DLL 中读取的元数据快照。</summary>
    /// <param name="DisplayVersion">展示用版本号，例如 <c>8.9</c>。</param>
    /// <param name="BuildId">构建标识，例如 <c>20260430.1</c>。</param>
    /// <param name="RegistryDefaults">DLL 自我描述的注册表默认值清单。</param>
    internal sealed record Metadata(
        string DisplayVersion,
        string BuildId,
        IReadOnlyList<RegistryDefault> RegistryDefaults);

    /// <summary>
    /// 从指定 DLL 文件读取所有部署所需的元数据。
    /// </summary>
    /// <param name="dllPath">已释放到磁盘的插件 DLL 完整路径。</param>
    /// <param name="metadata">读取成功时输出的元数据。</param>
    /// <param name="errorMessage">读取失败时的原因，成功时为 null。</param>
    /// <returns>true 表示读取成功。</returns>
    internal static bool TryRead(
        string dllPath,
        out Metadata? metadata,
        out string? errorMessage)
    {
        metadata = null;
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();

            // AssemblyInformationalVersion 形如 "8.9+20260430.1"
            var informational = ReadAssemblyInformationalVersion(reader)
                                ?? ReadAssemblyVersion(reader)
                                ?? "0.0";
            var (display, build) = SplitInformationalVersion(informational);

            var defaults = ReadRegistryDefaults(reader);

            metadata = new Metadata(display, build, defaults);
            errorMessage = null;
            return true;
        }
        catch (System.Exception ex)
        {
            errorMessage = $"读取插件元数据失败：{ex.Message}";
            return false;
        }
    }

    private static IReadOnlyList<RegistryDefault> ReadRegistryDefaults(MetadataReader reader)
    {
        var list = new List<RegistryDefault>();
        if (!reader.IsAssembly) return list;

        var asm = reader.GetAssemblyDefinition();
        foreach (var attrHandle in asm.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            var attrTypeName = GetAttributeTypeFullName(reader, attr);

            if (attrTypeName == RegistryDefaultStringAttr)
            {
                // ctor(string name, string value)
                var blob = reader.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 0x0001) continue;
                var name = blob.ReadSerializedString();
                var value = blob.ReadSerializedString();
                if (name is null) continue;
                list.Add(new RegistryDefault(name, RegistryDefaultKind.String, value ?? string.Empty, 0));
            }
            else if (attrTypeName == RegistryDefaultDwordAttr)
            {
                // ctor(string name, int value)
                var blob = reader.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 0x0001) continue;
                var name = blob.ReadSerializedString();
                var value = blob.ReadInt32();
                if (name is null) continue;
                list.Add(new RegistryDefault(name, RegistryDefaultKind.Dword, null, value));
            }
            else if (attrTypeName == RegistryDefaultDwordAtAttr)
            {
                // ctor(string subPath, string name, int value) + named props
                var blob = reader.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 0x0001) continue;
                var subPath = blob.ReadSerializedString();
                var name = blob.ReadSerializedString();
                var value = blob.ReadInt32();
                if (subPath is null || name is null) continue;

                bool forceOverwrite = false;
                bool removeOnUninstall = false;
                try
                {
                    var namedCount = blob.ReadUInt16();
                    for (int i = 0; i < namedCount; i++)
                    {
                        // 0x54 = property, 0x53 = field
                        var kind = blob.ReadByte();
                        var elemType = blob.ReadByte(); // 0x02 = ELEMENT_TYPE_BOOLEAN
                        var propName = blob.ReadSerializedString();
                        if (elemType != 0x02) { _ = blob.ReadByte(); continue; }
                        var propValue = blob.ReadBoolean();
                        if (propName == "ForceOverwrite") forceOverwrite = propValue;
                        else if (propName == "RemoveOnUninstall") removeOnUninstall = propValue;
                    }
                }
                catch
                {
                    // 命名参数解析失败时按默认值处理，避免单条声明拖垮整个清单。
                }

                list.Add(new RegistryDefault(
                    name, RegistryDefaultKind.DwordAt, null, value,
                    SubPath: subPath,
                    ForceOverwrite: forceOverwrite,
                    RemoveOnUninstall: removeOnUninstall));
            }
        }
        return list;
    }

    private static string? ReadAssemblyInformationalVersion(MetadataReader reader)
    {
        if (!reader.IsAssembly) return null;
        var asm = reader.GetAssemblyDefinition();
        foreach (var attrHandle in asm.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            if (GetAttributeTypeFullName(reader, attr) != InformationalVersionAttr) continue;

            // 自定义特性 blob：prolog (0x0001) + 序列化的 string 参数
            var blob = reader.GetBlobReader(attr.Value);
            if (blob.ReadUInt16() != 0x0001) return null;
            return blob.ReadSerializedString();
        }
        return null;
    }

    private static string? ReadAssemblyVersion(MetadataReader reader)
    {
        if (!reader.IsAssembly) return null;
        return reader.GetAssemblyDefinition().Version.ToString();
    }

    private static string GetAttributeTypeFullName(MetadataReader reader, CustomAttribute attr)
    {
        // 自定义特性的 ctor 句柄要么是 MemberRef（外部程序集中的特性类型），
        // 要么是 MethodDef（同一程序集内定义的特性类型）。
        EntityHandle ctor = attr.Constructor;
        EntityHandle parent;
        switch (ctor.Kind)
        {
            case HandleKind.MemberReference:
                parent = reader.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                break;
            case HandleKind.MethodDefinition:
                parent = reader.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType();
                break;
            default:
                return string.Empty;
        }

        return GetTypeFullName(reader, parent);
    }

    private static string GetTypeFullName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var tr = reader.GetTypeReference((TypeReferenceHandle)handle);
                var trNs = reader.GetString(tr.Namespace);
                var trName = reader.GetString(tr.Name);
                return string.IsNullOrEmpty(trNs) ? trName : trNs + "." + trName;
            case HandleKind.TypeDefinition:
                var td = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                var tdNs = reader.GetString(td.Namespace);
                var tdName = reader.GetString(td.Name);
                return string.IsNullOrEmpty(tdNs) ? tdName : tdNs + "." + tdName;
            default:
                return string.Empty;
        }
    }

    private static (string display, string build) SplitInformationalVersion(string full)
    {
        var idx = full.IndexOf('+');
        return idx >= 0
            ? (full[..idx], idx + 1 < full.Length ? full[(idx + 1)..] : string.Empty)
            : (full, string.Empty);
    }
}
