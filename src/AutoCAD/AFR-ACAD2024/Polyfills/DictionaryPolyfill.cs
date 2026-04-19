#if !NET5_0_OR_GREATER
// Polyfill: Dictionary.TryAdd 和 KeyValuePair 解构仅 .NET Core 2+ 可用
// ReSharper disable once CheckNamespace
namespace System.Collections.Generic
{
    internal static class Net48DictionaryExtensions
    {
        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue value)
            where TKey : notnull
        {
            if (dict.ContainsKey(key)) return false;
            dict.Add(key, value);
            return true;
        }

        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kvp, out TKey key, out TValue value)
        {
            key = kvp.Key;
            value = kvp.Value;
        }
    }
}
#endif
