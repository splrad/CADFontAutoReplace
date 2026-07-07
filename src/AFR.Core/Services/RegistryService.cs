using Microsoft.Win32;

namespace AFR.Services;

/// <summary>
/// 底层 Windows 注册表读写工具类，不承载业务逻辑。
/// <para>
/// 提供类型安全的 string / DWORD 读写，以及子键枚举、存在性检查、删除等操作。
/// 读取与查询方法在遇到注册表异常时会静默处理，并返回安全默认值。
/// </para>
/// </summary>
public static class RegistryService
{
    /// <summary>从注册表读取字符串值。找不到或读取失败时返回 null。</summary>
    public static string? ReadString(RegistryKey baseKey, string subKeyPath, string valueName)
    {
        try
        {
            using var key = baseKey.OpenSubKey(subKeyPath, false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从注册表读取 DWORD（int）值。找不到、类型不匹配或读取失败时返回 null。</summary>
    public static int? ReadDword(RegistryKey baseKey, string subKeyPath, string valueName)
    {
        try
        {
            using var key = baseKey.OpenSubKey(subKeyPath, false);
            var value = key?.GetValue(valueName);
            return value is int intVal ? intVal : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>向注册表写入字符串值。若子键不存在则自动创建。</summary>
    public static void WriteString(RegistryKey baseKey, string subKeyPath, string valueName, string value)
    {
        using var key = baseKey.CreateSubKey(subKeyPath, true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    /// <summary>向注册表写入 DWORD（int）值。若子键不存在则自动创建。</summary>
    public static void WriteDword(RegistryKey baseKey, string subKeyPath, string valueName, int value)
    {
        using var key = baseKey.CreateSubKey(subKeyPath, true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    /// <summary>检查指定注册表子键是否存在。</summary>
    public static bool KeyExists(RegistryKey baseKey, string subKeyPath)
    {
        try
        {
            using var key = baseKey.OpenSubKey(subKeyPath, false);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>检查指定子键下的值是否存在。</summary>
    public static bool ValueExists(RegistryKey baseKey, string subKeyPath, string valueName)
    {
        try
        {
            using var key = baseKey.OpenSubKey(subKeyPath, false);
            return key?.GetValue(valueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取指定子键下的所有直接子键名称。找不到或读取失败时返回空数组。</summary>
    public static string[] GetSubKeyNames(RegistryKey baseKey, string subKeyPath)
    {
        try
        {
            using var key = baseKey.OpenSubKey(subKeyPath, false);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 递归删除指定路径的注册表子键及其所有子项和值。
    /// 若子键不存在或删除失败，则返回 false。
    /// </summary>
    /// <returns>删除成功返回 true，否则返回 false。</returns>
    public static bool DeleteSubKeyTree(RegistryKey baseKey, string subKeyPath)
    {
        try
        {
            baseKey.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
