using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace AFR.GlyphCore.TextRepair;

internal static class GlyphCoreTextRepairCandidateGenerator
{
#if !NETFRAMEWORK
    private static int _providerRegistered;
#endif

    public static IReadOnlyList<GlyphCoreTextRepairCandidate> BuildCandidates(string currentText)
        => BuildCandidates(currentText, null);

    public static IReadOnlyList<GlyphCoreTextRepairCandidate> BuildCandidates(
        string currentText,
        GlyphCoreTextRepairContext? context)
    {
        var candidates = new List<GlyphCoreTextRepairCandidate>();
        AddCandidate(candidates, currentText, "current-noop", "当前文本", isRoundTrip: true);

        string preferredSource = GetEvidencePreferredSource(context);
        bool allowPrivateUsePrefixCleanup = HasNativeDecodeMismatch(context);
        AddPrivateUsePrefixSpaceFillCandidate(candidates, currentText, "private-use-prefix-space-fill", "native-evidence-leading-private-use-placeholder", allowPrivateUsePrefixCleanup);
        AddHookRawCandidate(candidates, currentText, context, allowPrivateUsePrefixCleanup);
        AddPreferredConversion(candidates, currentText, preferredSource, allowPrivateUsePrefixCleanup);

        if (!string.Equals(preferredSource, "big5-carrier-to-gbk", StringComparison.OrdinalIgnoreCase))
            TryAddConversion(candidates, currentText, 950, 936, "big5-carrier-to-gbk", allowPrivateUsePrefixCleanup);
        if (!string.Equals(preferredSource, "gbk-carrier-to-big5", StringComparison.OrdinalIgnoreCase))
            TryAddConversion(candidates, currentText, 936, 950, "gbk-carrier-to-big5", allowPrivateUsePrefixCleanup);
        if (!string.Equals(preferredSource, "utf8-carrier-to-gbk", StringComparison.OrdinalIgnoreCase))
            TryAddConversion(candidates, currentText, Encoding.UTF8.CodePage, 936, "utf8-carrier-to-gbk", allowPrivateUsePrefixCleanup);
        if (!string.Equals(preferredSource, "gbk-carrier-to-utf8", StringComparison.OrdinalIgnoreCase))
            TryAddConversion(candidates, currentText, 936, Encoding.UTF8.CodePage, "gbk-carrier-to-utf8", allowPrivateUsePrefixCleanup);

        return candidates
            .OrderBy(c => c.IsNoOp ? 1 : 0)
            .ThenBy(c => CandidateSourcePriority(c.Source, preferredSource))
            .ThenBy(c => c.Text, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddPreferredConversion(
        List<GlyphCoreTextRepairCandidate> candidates,
        string currentText,
        string preferredSource,
        bool allowPrivateUsePrefixCleanup)
    {
        if (string.IsNullOrEmpty(preferredSource))
            return;

        if (string.Equals(preferredSource, "big5-carrier-to-gbk", StringComparison.OrdinalIgnoreCase))
            TryAddConversion(candidates, currentText, 950, 936, preferredSource, allowPrivateUsePrefixCleanup);
        else if (string.Equals(preferredSource, "gbk-carrier-to-big5", StringComparison.OrdinalIgnoreCase))
            TryAddConversion(candidates, currentText, 936, 950, preferredSource, allowPrivateUsePrefixCleanup);
        else if (string.Equals(preferredSource, "utf8-carrier-to-gbk", StringComparison.OrdinalIgnoreCase))
            TryAddConversion(candidates, currentText, Encoding.UTF8.CodePage, 936, preferredSource, allowPrivateUsePrefixCleanup);
        else if (string.Equals(preferredSource, "gbk-carrier-to-utf8", StringComparison.OrdinalIgnoreCase))
            TryAddConversion(candidates, currentText, 936, Encoding.UTF8.CodePage, preferredSource, allowPrivateUsePrefixCleanup);
    }

    private static string GetEvidencePreferredSource(GlyphCoreTextRepairContext? context)
    {
        if (context == null || !context.NativeDecodeFamilyMismatch)
            return string.Empty;

        string source = context.NativeDecodeSourceCodePageFamily ?? string.Empty;
        string applied = context.NativeDecodeAppliedCodePageFamily ?? string.Empty;
        if (ContainsFamily(source, "big5") && ContainsFamily(applied, "gbk"))
            return "gbk-carrier-to-big5";
        if (ContainsFamily(source, "gbk") && ContainsFamily(applied, "big5"))
            return "big5-carrier-to-gbk";
        if (ContainsFamily(source, "utf8") && ContainsFamily(applied, "gbk"))
            return "gbk-carrier-to-utf8";
        if (ContainsFamily(source, "gbk") && ContainsFamily(applied, "utf8"))
            return "utf8-carrier-to-gbk";

        return string.Empty;
    }

    private static int CandidateSourcePriority(string source, string preferredSource)
    {
        if (ContainsSource(source, "hook-raw-stream"))
            return 0;

        if (string.IsNullOrEmpty(preferredSource))
            return 1;

        return ContainsSource(source, preferredSource) ? 0 : 1;
    }

    private static void AddHookRawCandidate(
        List<GlyphCoreTextRepairCandidate> candidates,
        string currentText,
        GlyphCoreTextRepairContext? context,
        bool allowPrivateUsePrefixCleanup)
    {
        if (context == null || !context.HasHookRawDecodeEvidence)
            return;

        string text = context.HookPreferredDecodedText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, currentText, StringComparison.Ordinal))
            return;

        string source = string.IsNullOrWhiteSpace(context.HookRawCandidateSource)
            ? "hook-raw-stream"
            : context.HookRawCandidateSource;
        if (!ContainsSource(source, "hook-raw-stream"))
            source = "hook-raw-stream+" + source;

        string reason = context.HookRawRoundTrip
            ? "hook-raw-roundtrip-ok"
            : "hook-raw-derived";
        if (context.HookRawPayloadLength > 0)
            reason += $"; raw-len={context.HookRawPayloadLength}";

        AddCandidate(candidates, text, source, reason, context.HookRawRoundTrip);
        AddPrivateUsePunctuationCarryoverCandidate(
            candidates,
            currentText,
            text,
            source + "+private-use-punctuation-carryover",
            reason + "; private-use-punctuation-carryover",
            allowPrivateUsePrefixCleanup && context.HookRawRoundTrip);
        AddPrivateUsePrefixSpaceFillCandidate(
            candidates,
            text,
            source + "+private-use-prefix-space-fill",
            reason + "; leading-private-use-placeholder",
            allowPrivateUsePrefixCleanup && context.HookRawRoundTrip);
    }

    private static void TryAddConversion(
        List<GlyphCoreTextRepairCandidate> candidates,
        string currentText,
        int carrierCodePage,
        int targetCodePage,
        string source,
        bool allowPrivateUsePrefixCleanup)
    {
        if (string.IsNullOrEmpty(currentText))
            return;

        if (!TryConvertCarrier(currentText, carrierCodePage, targetCodePage, out string candidate, out bool roundTrip, out string reason))
            return;

        AddCandidate(candidates, candidate, source, reason, roundTrip);
        AddPrivateUsePunctuationCarryoverCandidate(
            candidates,
            currentText,
            candidate,
            source + "+private-use-punctuation-carryover",
            reason + "; private-use-punctuation-carryover",
            allowPrivateUsePrefixCleanup && roundTrip);
        AddPrivateUsePrefixSpaceFillCandidate(
            candidates,
            candidate,
            source + "+private-use-prefix-space-fill",
            reason + "; leading-private-use-placeholder",
            allowPrivateUsePrefixCleanup && roundTrip);
    }

    private static void AddPrivateUsePrefixSpaceFillCandidate(
        List<GlyphCoreTextRepairCandidate> candidates,
        string text,
        string source,
        string reason,
        bool allowPrivateUsePrefixCleanup)
    {
        if (!allowPrivateUsePrefixCleanup)
            return;

        if (!TryReplaceLeadingPrivateUsePlaceholdersWithSpaces(text, out string candidate))
            return;

        AddCandidate(candidates, candidate, source, reason, isRoundTrip: true);
    }

    private static void AddPrivateUsePunctuationCarryoverCandidate(
        List<GlyphCoreTextRepairCandidate> candidates,
        string currentText,
        string text,
        string source,
        string reason,
        bool allowPrivateUsePunctuationCarryover)
    {
        if (!allowPrivateUsePunctuationCarryover)
            return;

        if (!TryReplaceInteriorPrivateUseWithCurrentPunctuation(currentText, text, out string candidate))
            return;

        AddCandidate(candidates, candidate, source, reason, isRoundTrip: true);
    }

    private static bool TryReplaceLeadingPrivateUsePlaceholdersWithSpaces(string text, out string candidate)
    {
        candidate = string.Empty;
        if (string.IsNullOrEmpty(text))
            return false;

        int prefixLength = 0;
        while (prefixLength < text.Length
               && prefixLength < 4
               && IsPrivateUse(text[prefixLength]))
        {
            prefixLength++;
        }

        if (prefixLength == 0 || prefixLength >= text.Length)
            return false;

        if (prefixLength < text.Length && IsPrivateUse(text[prefixLength]))
            return false;

        string visibleText = text.Substring(prefixLength);
        if (string.IsNullOrWhiteSpace(visibleText) || !HasMeaningfulVisibleText(visibleText))
            return false;

        candidate = new string(' ', prefixLength) + visibleText;
        return true;
    }

    private static bool TryReplaceInteriorPrivateUseWithCurrentPunctuation(string currentText, string text, out string candidate)
    {
        candidate = string.Empty;
        if (string.IsNullOrEmpty(currentText)
            || string.IsNullOrEmpty(text)
            || currentText.Length != text.Length
            || IsPrivateUse(text[0]))
            return false;

        char[] chars = text.ToCharArray();
        int replacements = 0;
        for (int i = 0; i < chars.Length; i++)
        {
            if (!IsPrivateUse(chars[i]))
                continue;

            char carryover = currentText[i];
            if (!IsCarryoverPunctuation(carryover))
                return false;

            chars[i] = carryover;
            replacements++;
        }

        if (replacements == 0)
            return false;

        string repaired = new string(chars);
        if (!HasMeaningfulVisibleText(repaired))
            return false;

        candidate = repaired;
        return true;
    }

    private static bool IsCarryoverPunctuation(char ch)
    {
        if (char.IsWhiteSpace(ch)
            || char.IsControl(ch)
            || IsPrivateUse(ch)
            || char.IsLetterOrDigit(ch))
            return false;

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category == UnicodeCategory.OtherPunctuation
               || category == UnicodeCategory.ConnectorPunctuation
               || category == UnicodeCategory.DashPunctuation
               || category == UnicodeCategory.OpenPunctuation
               || category == UnicodeCategory.ClosePunctuation
               || category == UnicodeCategory.InitialQuotePunctuation
               || category == UnicodeCategory.FinalQuotePunctuation
               || category == UnicodeCategory.MathSymbol
               || category == UnicodeCategory.ModifierSymbol
               || category == UnicodeCategory.OtherSymbol;
    }

    private static bool HasMeaningfulVisibleText(string text)
    {
        bool sawVisible = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
                continue;

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.PrivateUse
                || category == UnicodeCategory.Surrogate
                || category == UnicodeCategory.OtherNotAssigned)
                return false;

            sawVisible = true;
            if (char.IsLetterOrDigit(ch)
                || category == UnicodeCategory.OtherLetter
                || category == UnicodeCategory.LetterNumber
                || category == UnicodeCategory.DecimalDigitNumber)
                return true;
        }

        return sawVisible && text.Length <= 2;
    }

    private static bool HasNativeDecodeMismatch(GlyphCoreTextRepairContext? context)
    {
        return context != null
               && context.HasNativeDecodeEvidence
               && context.NativeDecodeFamilyMismatch;
    }

    private static bool TryConvertCarrier(
        string currentText,
        int carrierCodePage,
        int targetCodePage,
        out string candidate,
        out bool roundTrip,
        out string reason)
    {
        candidate = string.Empty;
        roundTrip = false;
        reason = string.Empty;

        try
        {
            EnsureCodePages();
            Encoding carrier = GetStrictEncoding(carrierCodePage);
            Encoding target = GetStrictEncoding(targetCodePage);

            byte[] carrierBytes = carrier.GetBytes(currentText);
            string decoded = target.GetString(carrierBytes);
            if (string.IsNullOrEmpty(decoded) || string.Equals(decoded, currentText, StringComparison.Ordinal))
            {
                reason = "same";
                return false;
            }

            byte[] candidateBytes = target.GetBytes(decoded);
            roundTrip = carrierBytes.SequenceEqual(candidateBytes)
                        && string.Equals(carrier.GetString(candidateBytes), currentText, StringComparison.Ordinal);
            candidate = decoded;
            reason = roundTrip ? "roundtrip-ok" : "roundtrip-failed";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }
    }

    private static Encoding GetStrictEncoding(int codePage)
    {
        if (codePage == Encoding.UTF8.CodePage)
            return new UTF8Encoding(false, throwOnInvalidBytes: true);

        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static void EnsureCodePages()
    {
#if !NETFRAMEWORK
        if (Interlocked.Exchange(ref _providerRegistered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
    }

    private static void AddCandidate(
        List<GlyphCoreTextRepairCandidate> candidates,
        string text,
        string source,
        string reason,
        bool isRoundTrip)
    {
        if (string.IsNullOrEmpty(text))
            return;

        GlyphCoreTextRepairCandidate? existing = candidates.FirstOrDefault(c => string.Equals(c.Text, text, StringComparison.Ordinal));
        if (existing != null)
        {
            existing.AddSource(source, reason, isRoundTrip);
            return;
        }

        candidates.Add(new GlyphCoreTextRepairCandidate(text, source, reason, isRoundTrip));
    }

    private static bool ContainsSource(string source, string token)
    {
        return !string.IsNullOrEmpty(source)
               && source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ContainsFamily(string value, string family)
    {
        return !string.IsNullOrEmpty(value)
               && value.IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsPrivateUse(char ch)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category == UnicodeCategory.PrivateUse
               || (ch >= '\uE000' && ch <= '\uF8FF');
    }
}

