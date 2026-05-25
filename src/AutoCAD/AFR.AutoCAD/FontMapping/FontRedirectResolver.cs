using System;
using AFR.Services;

namespace AFR.FontMapping;

internal enum FontRedirectKind
{
    TrueType,
    ShxMain,
    ShxBigFont
}

internal static class FontRedirectResolver
{
    /// <summary>
    /// 提取规范文件名：修剪空白，去掉路径前缀（等价于 Path.GetFileName），
    /// 避免空结果时回退为整段路径。
    /// 返回的 (start, length) 是原始字符串中有效文件名的切片范围。
    /// </summary>
    private static (int Start, int Length) NormalizeInputNameRange(string fontName)
    {
        if (fontName == null || fontName.Length == 0)
            return (0, 0);

        int lo = 0, hi = fontName.Length - 1;
        while (lo <= hi && fontName[lo] <= ' ') lo++;
        while (hi >= lo && fontName[hi] <= ' ') hi--;
        if (lo > hi)
            return (0, 0);

        // 找最后一个路径分隔符（等价于 Path.GetFileName 语义）
        int sep = -1;
        for (int i = hi; i >= lo; i--)
        {
            char c = fontName[i];
            if (c == '/' || c == '\\') { sep = i; break; }
        }

        if (sep < 0)
            return (lo, hi - lo + 1);

        // 等价于 Path.GetFileName：分隔符在末尾时返回空串，Path 返回""，
        // 原始实现用 IsNullOrWhiteSpace 回退到 trimmed；这里保持一致。
        int start = sep + 1;
        int len   = hi - sep;
        return len > 0 ? (start, len) : (lo, hi - lo + 1);
    }

    internal static string NormalizeInputName(string fontName)
    {
        var (start, length) = NormalizeInputNameRange(fontName);
        if (length == 0) return string.Empty;
        // 仅当子串与原串不同时才分配
        return length == fontName.Length ? fontName : fontName.Substring(start, length);
    }

    internal static bool HasAtPrefix(string fontName)
    {
        var (start, length) = NormalizeInputNameRange(fontName);
        return length > 1 && fontName[start] == '@';
    }

    internal static string StripLeadingAtPrefix(string fontName)
    {
        var (start, length) = NormalizeInputNameRange(fontName);
        if (length == 0) return string.Empty;
        if (fontName[start] == '@') { start++; length--; }
        return length == 0 ? string.Empty : fontName.Substring(start, length);
    }

    internal static bool TryResolveConfiguredReplacement(
        FontRedirectKind kind,
        out string replacement)
    {
        replacement = string.Empty;

        string raw = kind switch
        {
            FontRedirectKind.TrueType    => ConfigService.Instance.TrueTypeFont?.Trim() ?? string.Empty,
            FontRedirectKind.ShxBigFont  => ConfigService.Instance.BigFont?.Trim()      ?? string.Empty,
            _                            => ConfigService.Instance.MainFont?.Trim()     ?? string.Empty
        };

        // 一次 Substring 得到规范名，去掉可能的 @ 前缀
        var (rs, rl) = NormalizeInputNameRange(raw);
        if (rl == 0) return false;
        if (raw[rs] == '@') { rs++; rl--; }
        if (rl == 0) return false;
        string configured = raw.Substring(rs, rl);

        if (kind == FontRedirectKind.TrueType)
        {
            if (!IsAvailableTrueType(configured))
                return false;

            replacement = configured;
            return true;
        }

        string shxName = EnsureShx(configured);
        if (!ShxFontAvailabilityIndex.IsAvailableWithAtFallback(shxName))
            return false;

        if (ShxFontAvailabilityIndex.TryGetKind(shxName, out bool isBigFont))
        {
            if (kind == FontRedirectKind.ShxBigFont && !isBigFont)
                return false;
            if (kind == FontRedirectKind.ShxMain && isBigFont)
                return false;
        }
        else
        {
            return false;
        }

        replacement = shxName;
        return true;
    }

    internal static bool IsAvailableTrueType(string name)
    {
        var (start, length) = NormalizeInputNameRange(name);
        if (length == 0) return false;
        string normalized = length == name.Length ? name : name.Substring(start, length);
        return TrueTypeFontAvailabilityIndex.IsAvailable(normalized);
    }

    internal static bool IsTrueTypeFontFileName(string fontName)
        => FontDetector.IsTrueTypeFontFile(NormalizeInputName(fontName));

    internal static string EnsureShx(string name)
    {
        var (start, length) = NormalizeInputNameRange(name);
        if (length == 0) return ".shx";

        // 去 @ 前缀（索引操作，不分配）
        if (name[start] == '@') { start++; length--; }
        if (length == 0) return ".shx";

        // 检查是否已有 .shx 后缀（不产生新字符串）
        if (length >= 4
            && string.Compare(name, start + length - 4, ".shx", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
        {
            return name.Substring(start, length);
        }

#if NET7_0_OR_GREATER
        return string.Concat(name.AsSpan(start, length), ".shx".AsSpan());
#else
        return name.Substring(start, length) + ".shx";
#endif
    }
}
