#if !NET5_0_OR_GREATER
// Polyfill: string.Contains(string, StringComparison) 仅 .NET Core+ 可用
// ReSharper disable once CheckNamespace
namespace System;

internal static class Net48StringExtensions
{
    public static bool Contains(this string source, string value, StringComparison comparison)
    {
        return source.IndexOf(value, comparison) >= 0;
    }

    public static string[] Split(this string source, char separator, StringSplitOptions options)
    {
        return source.Split(new[] { separator }, options);
    }
}
#endif
