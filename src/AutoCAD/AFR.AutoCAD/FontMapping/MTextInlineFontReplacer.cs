using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using AFR.Models;
using AFR.Services;

namespace AFR.FontMapping;

/// <summary>
/// 将 MText 实体中缺失的 TrueType 内联字体（\f 格式代码）转换为 SHX 格式（\F 格式代码）。
/// <para>
/// MText 内联 \f TrueType 字体由 GDI/DirectWrite 直接渲染，不经过 ldfile，
/// ldfile Hook 无法拦截。通过将 \f 转换为 \F（指向用户配置的 SHX 主字体+大字体），
/// 使后续渲染走 ldfile 路径，由 Hook 统一管理。
/// </para>
/// </summary>
internal static class MTextInlineFontReplacer
{
    /// <summary>
    /// 将数据库中所有 MText 实体的缺失 TrueType 内联字体转换为 SHX 格式。
    /// </summary>
    internal static List<InlineFontFixRecord> ConvertMissingTrueTypeToShx(
        Database db,
        Dictionary<string, InlineFontType> inlineFonts,
        FontDetectionContext context,
        string mainFont, string bigFont)
    {
        var missingTrueType = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fontName, inlineType) in inlineFonts)
        {
            if (inlineType == InlineFontType.TrueType
                && !FontDetector.IsTrueTypeFontAvailable(fontName, context)
                && !ContainsGarbledChars(fontName))
            {
                missingTrueType.Add(fontName);
            }
        }

        if (missingTrueType.Count == 0) return new List<InlineFontFixRecord>();

        string shxMain = StripShx(mainFont);
        string shxBig = StripShx(bigFont);
        string shxSpec = !string.IsNullOrEmpty(shxBig)
            ? $"{shxMain},{shxBig}"
            : shxMain;

        if (string.IsNullOrEmpty(shxMain) && string.IsNullOrEmpty(shxBig))
        {
            DiagnosticLogger.Log("MText替换", "未配置 SHX 替换字体，跳过 TrueType→SHX 转换");
            return new List<InlineFontFixRecord>();
        }

        int modifiedCount = 0;
        using var tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId btrId in bt)
        {
            try
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                foreach (ObjectId entId in btr)
                {
                    try
                    {
                        if (tr.GetObject(entId, OpenMode.ForRead) is MText mtext)
                        {
                            string original = mtext.Contents;
                            if (string.IsNullOrEmpty(original)) continue;

                            string replaced = ApplyConversion(original, missingTrueType, shxSpec);
                            if (!ReferenceEquals(replaced, original))
                            {
                                mtext.UpgradeOpen();
                                mtext.Contents = replaced;
                                modifiedCount++;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        tr.Commit();
        DiagnosticLogger.Log("MText替换",
            $"TrueType→SHX 转换: {missingTrueType.Count} 个字体, {modifiedCount} 个 MText 实体");

        string displayRep = !string.IsNullOrEmpty(shxBig) ? EnsureShx(shxBig) : EnsureShx(shxMain);
        var records = new List<InlineFontFixRecord>(missingTrueType.Count);
        foreach (var fontName in missingTrueType)
            records.Add(new InlineFontFixRecord(fontName, displayRep, "MText内联", "TrueType→SHX"));

        return records;
    }

    #region Contents 转换

    private static string ApplyConversion(
        string contents, HashSet<string> missingFonts, string shxSpec)
    {
        int len = contents.Length;
        if (contents.IndexOf('\\') < 0) return contents;

        var sb = new StringBuilder(len + 32);
        int i = 0;
        bool modified = false;

        while (i < len)
        {
            if (i < len - 1 && contents[i] == '\\')
            {
                char code = contents[i + 1];

                if (code == '\\')
                {
                    sb.Append('\\').Append('\\');
                    i += 2;
                }
                else if (code == 'f')
                {
                    i += 2;
                    modified |= ProcessTrueTypeSegment(contents, ref i, sb, missingFonts, shxSpec);
                }
                else
                {
                    sb.Append(contents[i]).Append(contents[i + 1]);
                    i += 2;
                }
            }
            else
            {
                sb.Append(contents[i]);
                i++;
            }
        }

        return modified ? sb.ToString() : contents;
    }

    /// <summary>
    /// 处理单个 \f 段。若字族名缺失 → 吃掉 \fName|params; 并输出 \FshxSpec|。
    /// </summary>
    private static bool ProcessTrueTypeSegment(
        string text, ref int i, StringBuilder sb,
        HashSet<string> missingFonts, string shxSpec)
    {
        int len = text.Length;
        int nameStart = i;

        while (i < len && text[i] != '|' && text[i] != ';' && text[i] != '\\')
            i++;

        string fontName = text[nameStart..i].Trim();

        if (fontName.Length > 0 && missingFonts.Contains(fontName))
        {
            // 吃掉 TrueType 参数 |bN|iN|cN|pN;
            if (i < len && text[i] == '|')
            {
                if (IsParameterStart(text, i + 1))
                {
                    i++;
                    while (i < len && text[i] != ';' && text[i] != '\\')
                        i++;
                    if (i < len && text[i] == ';')
                        i++;
                }
                else
                {
                    i++;
                }
            }
            else if (i < len && text[i] == ';')
            {
                i++;
            }

            sb.Append('\\').Append('F').Append(shxSpec).Append('|');
            return true;
        }

        // 不需要转换 → 原样输出
        sb.Append('\\').Append('f');
        sb.Append(text, nameStart, i - nameStart);

        if (i < len)
        {
            if (text[i] == ';')
            {
                sb.Append(';');
                i++;
            }
            else if (text[i] == '|')
            {
                if (IsParameterStart(text, i + 1))
                {
                    int paramStart = i;
                    i++;
                    while (i < len && text[i] != ';' && text[i] != '\\')
                        i++;
                    if (i < len && text[i] == ';')
                        i++;
                    sb.Append(text, paramStart, i - paramStart);
                }
                else
                {
                    sb.Append('|');
                    i++;
                }
            }
        }

        return false;
    }

    #endregion

    #region 辅助方法

    private static bool IsParameterStart(string text, int pos)
    {
        if (pos >= text.Length) return false;
        char c = text[pos];
        if (c is not ('b' or 'i' or 'c' or 'p')) return false;
        int next = pos + 1;
        return next < text.Length && text[next] >= '0' && text[next] <= '9';
    }

    private static string StripShx(string name) =>
        string.IsNullOrEmpty(name) ? "" :
        name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string EnsureShx(string name) =>
        name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ? name : name + ".shx";

    /// <summary>
    /// 检查字体名是否包含编码异常字符（U+FE00 以上）。
    /// DWG 文件使用旧版编码（GBK 等）时，MText Contents 中的中文字体名
    /// 可能被错误解码为 U+FFxx 区域的无意义字符。此类字体名不是合法字体，
    /// 不应参与 \f→\F 转换或出现在 AFRLOG 面板中。
    /// </summary>
    private static bool ContainsGarbledChars(string fontName)
    {
        for (int i = 0; i < fontName.Length; i++)
        {
            if (fontName[i] >= '\uFE00')
                return true;
        }
        return false;
    }

    #endregion
}