using Microsoft.Win32;

namespace AFR_ACAD2026.Services;

/// <summary>
/// 底层注册表读写操作，不包含业务逻辑。
/// 提供类型安全的 string / DWORD 访问。
/// </summary>
internal static class RegistryService
{
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

    public static void WriteString(RegistryKey baseKey, string subKeyPath, string valueName, string value)
    {
        using var key = baseKey.CreateSubKey(subKeyPath, true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public static void WriteDword(RegistryKey baseKey, string subKeyPath, string valueName, int value)
    {
        using var key = baseKey.CreateSubKey(subKeyPath, true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

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

    public static string[] GetSubKeyNames(RegistryKey baseKey, string subKeyPath)
    {
        try
        {
            using var key = baseKey.OpenSubKey(subKeyPath, false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }
}
