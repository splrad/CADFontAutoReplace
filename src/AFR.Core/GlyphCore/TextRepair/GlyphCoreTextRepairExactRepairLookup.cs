using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairExactRepairLookup
{
    private static readonly Lazy<IReadOnlyList<Entry>> Entries = new(LoadEntries);

    public static bool TryFind(GlyphCoreTextRepairContext context, out string labelText, out string summary)
    {
        labelText = string.Empty;
        summary = string.Empty;

        string currentText = context.CurrentText ?? string.Empty;
        if (string.IsNullOrEmpty(currentText))
            return false;

        List<Entry> textMatches = Entries.Value
            .Where(entry => Same(entry.CurrentText, currentText))
            .ToList();
        if (textMatches.Count == 0)
            return false;

        IEnumerable<Entry> exactContextMatches = textMatches.Where(entry =>
            Same(entry.Layer, context.Layer)
            && Same(entry.TextStyleName, context.TextStyleName)
            && Same(entry.Font, context.TextStyleFileName)
            && Same(entry.BigFont, context.TextStyleBigFontFileName)
            && Same(entry.OwnerBlockName, context.OwnerBlockName));
        if (TryPickUnambiguous(exactContextMatches, "training-exact-context", out labelText, out summary))
            return true;

        IEnumerable<Entry> styleContextMatches = textMatches.Where(entry =>
            Same(entry.TextStyleName, context.TextStyleName)
            && Same(entry.Font, context.TextStyleFileName)
            && Same(entry.BigFont, context.TextStyleBigFontFileName));
        if (TryPickUnambiguous(styleContextMatches, "training-style-context", out labelText, out summary))
            return true;

        return TryPickUnambiguous(textMatches, "training-text-only", out labelText, out summary);
    }

    private static bool TryPickUnambiguous(IEnumerable<Entry> entries, string reason, out string labelText, out string summary)
    {
        labelText = string.Empty;
        summary = string.Empty;

        List<IGrouping<string, Entry>> groups = entries
            .Where(entry => !string.IsNullOrEmpty(entry.LabelText))
            .GroupBy(entry => entry.LabelText, StringComparer.Ordinal)
            .ToList();
        if (groups.Count != 1)
            return false;

        IGrouping<string, Entry> group = groups[0];
        labelText = group.Key;
        int sourceCount = group.Sum(entry => Math.Max(1, entry.SourceCount));
        summary = $"exact={reason}, sourceCount={sourceCount}";
        return true;
    }

    private static IReadOnlyList<Entry> LoadEntries()
    {
        try
        {
            Assembly owner = typeof(GlyphCoreTextRepairExactRepairLookup).Assembly;
            using Stream? stream = owner.GetManifestResourceStream(GlyphCoreTextRepairConstants.ExactRepairsResourceName);
            if (stream == null)
                return Array.Empty<Entry>();

            using var reader = new StreamReader(stream);
            JObject root = JObject.Parse(reader.ReadToEnd());
            if (!Same((string?)root["schema"], "dbtext-ai-exact-repairs-v1"))
                return Array.Empty<Entry>();
            if (!Same((string?)root["featureSchemaVersion"], GlyphCoreTextRepairConstants.FeatureSchemaVersion))
                return Array.Empty<Entry>();

            JArray? rows = root["entries"] as JArray;
            if (rows == null)
                return Array.Empty<Entry>();

            return rows
                .OfType<JObject>()
                .Select(ReadEntry)
                .Where(entry => !string.IsNullOrEmpty(entry.CurrentText)
                                && !string.IsNullOrEmpty(entry.LabelText)
                                && !Same(entry.CurrentText, entry.LabelText))
                .ToList();
        }
        catch
        {
            return Array.Empty<Entry>();
        }
    }

    private static Entry ReadEntry(JObject row) =>
        new(
            Text(row, "currentText"),
            Text(row, "labelText"),
            Text(row, "layer"),
            Text(row, "textStyleName"),
            Text(row, "font"),
            Text(row, "bigFont"),
            Text(row, "ownerBlockName"),
            (int?)row["sourceCount"] ?? 1);

    private static string Text(JObject row, string propertyName) =>
        ((string?)row[propertyName]) ?? string.Empty;

    private static bool Same(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private sealed record Entry(
        string CurrentText,
        string LabelText,
        string Layer,
        string TextStyleName,
        string Font,
        string BigFont,
        string OwnerBlockName,
        int SourceCount);
}
