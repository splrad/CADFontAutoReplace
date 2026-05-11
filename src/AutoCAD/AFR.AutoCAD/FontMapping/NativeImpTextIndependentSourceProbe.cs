#if DEBUG
using System.Runtime.InteropServices;
using System.Text;

namespace AFR.FontMapping;

/// <summary>
/// DEBUG-only native object scanner for independent DBText recovery sources.
/// <para>
/// This probe is intentionally read-only. It searches the same <c>AcDbImpText</c>
/// object for a second complete source, excluding the known current text pointer
/// at <c>+0x68</c>. A hit here is still diagnostic until the slot offset is
/// statically verified as a pre-corruption text source.
/// </para>
/// </summary>
internal static class NativeImpTextIndependentSourceProbe
{
    private const uint MemCommit = 0x1000;
    private const int ImpTextCurrentTextPointerOffset = 0x68;
    private const int MaxImpTextScanBytes = 0x800;
    private const int MaxWideStringChars = 512;
    private const int MaxPointerByteScanBytes = 1024;
    private const int MaxReportedSlots = 16;

    /// <summary>
    /// Inspect native <c>AcDbImpText</c> memory for object-scoped independent sources.
    /// </summary>
    public static NativeImpTextIndependentSourceReport Inspect(
        IntPtr impText,
        string currentText,
        string candidateText,
        byte[] candidateCarrierBytes)
    {
        var report = new NativeImpTextIndependentSourceReport();
        if (impText == IntPtr.Zero || !IsCommittedMemory(impText))
        {
            report.Decision = "unavailable invalid-imp-text";
            return report;
        }

        byte[] objectBytes = TryReadBytes(impText, MaxImpTextScanBytes);
        report.ScannedObjectBytes = objectBytes.Length;
        report.CandidateInlineWideOffset = IndexOfSequence(
            objectBytes,
            EncodeUtf16Bytes(candidateText),
            minimumNeedleLength: 4);
        report.CurrentInlineWideOffset = IndexOfSequence(
            objectBytes,
            EncodeUtf16Bytes(currentText),
            minimumNeedleLength: 4);

        ScanPointerSlots(impText, currentText, candidateText, candidateCarrierBytes, report);

        report.Decision = report.IndependentCandidateWideSlotCount > 0
            ? "diagnostic-only candidate-wide-slot-present-needs-static-offset-verification"
            : report.CandidateCarrierByteSlotCount > 0
                ? "diagnostic-only candidate-carrier-byte-slot-present-needs-static-buffer-verification"
                : "diagnostic-only no-independent-native-source";
        return report;
    }

    private static void ScanPointerSlots(
        IntPtr impText,
        string currentText,
        string candidateText,
        byte[] candidateCarrierBytes,
        NativeImpTextIndependentSourceReport report)
    {
        int pointerSize = IntPtr.Size;
        for (int offset = 0; offset <= MaxImpTextScanBytes - pointerSize; offset += pointerSize)
        {
            IntPtr slotAddress = impText + offset;
            if (!IsCommittedMemory(slotAddress))
                continue;

            IntPtr pointer;
            try
            {
                pointer = Marshal.ReadIntPtr(slotAddress);
            }
            catch
            {
                continue;
            }

            if (pointer == IntPtr.Zero || !IsCommittedMemory(pointer))
                continue;

            bool isCurrentTextPointerSlot = offset == ImpTextCurrentTextPointerOffset;
            TryRecordWideSlot(offset, pointer, isCurrentTextPointerSlot, currentText, candidateText, report);
            TryRecordCarrierByteSlot(offset, pointer, isCurrentTextPointerSlot, candidateCarrierBytes, report);
        }
    }

    private static void TryRecordWideSlot(
        int offset,
        IntPtr pointer,
        bool isCurrentTextPointerSlot,
        string currentText,
        string candidateText,
        NativeImpTextIndependentSourceReport report)
    {
        string text = ReadWideString(pointer, MaxWideStringChars, out bool truncated);
        if (string.IsNullOrEmpty(text) || !LooksLikeReadableString(text))
            return;

        bool matchesCurrent = string.Equals(text, currentText, StringComparison.Ordinal);
        bool matchesCandidate = string.Equals(text, candidateText, StringComparison.Ordinal);
        if (!matchesCurrent && !matchesCandidate)
            return;

        report.WideLikeSlotCount++;
        var slot = new NativeImpTextWideStringSlot(
            offset,
            pointer,
            text.Length,
            truncated,
            isCurrentTextPointerSlot,
            matchesCurrent,
            matchesCandidate,
            text);

        if (matchesCandidate && !isCurrentTextPointerSlot)
            report.IndependentCandidateWideSlotCount++;
        if (matchesCandidate)
            report.CandidateWideSlotCount++;
        if (matchesCurrent)
            report.CurrentWideSlotCount++;
        if (report.WideSlots.Count < MaxReportedSlots)
            report.WideSlots.Add(slot);
    }

    private static void TryRecordCarrierByteSlot(
        int offset,
        IntPtr pointer,
        bool isCurrentTextPointerSlot,
        byte[] candidateCarrierBytes,
        NativeImpTextIndependentSourceReport report)
    {
        if (candidateCarrierBytes.Length == 0)
            return;

        byte[] bytes = TryReadBytes(pointer, MaxPointerByteScanBytes);
        int index = IndexOfSequence(bytes, candidateCarrierBytes, minimumNeedleLength: 4);
        if (index < 0)
            return;

        report.CandidateCarrierByteSlotCount++;
        if (report.ByteSlots.Count < MaxReportedSlots)
        {
            report.ByteSlots.Add(new NativeImpTextByteSlot(
                offset,
                pointer,
                bytes.Length,
                index,
                isCurrentTextPointerSlot,
                candidateCarrierBytes.Length));
        }
    }

    private static string ReadWideString(IntPtr source, int maxChars, out bool truncated)
    {
        truncated = false;
        if (source == IntPtr.Zero || maxChars <= 0 || !IsCommittedMemory(source))
            return string.Empty;

        try
        {
            var chars = new List<char>(Math.Min(maxChars, 128));
            for (int i = 0; i < maxChars; i++)
            {
                IntPtr current = source + (i * 2);
                if (!IsCommittedMemory(current))
                    break;

                char ch = (char)Marshal.ReadInt16(current);
                if (ch == '\0')
                    return new string(chars.ToArray());

                chars.Add(ch);
            }

            truncated = chars.Count >= maxChars;
            return new string(chars.ToArray());
        }
        catch
        {
            truncated = false;
            return string.Empty;
        }
    }

    private static byte[] TryReadBytes(IntPtr source, int maxBytes)
    {
        if (source == IntPtr.Zero || maxBytes <= 0 || !IsCommittedMemory(source))
            return [];

        try
        {
            var bytes = new byte[maxBytes];
            int count = 0;
            for (; count < maxBytes; count++)
            {
                IntPtr current = source + count;
                if (!IsCommittedMemory(current))
                    break;

                bytes[count] = Marshal.ReadByte(current);
            }

            if (count != bytes.Length)
                Array.Resize(ref bytes, count);
            return bytes;
        }
        catch
        {
            return [];
        }
    }

    private static byte[] EncodeUtf16Bytes(string text)
    {
        return string.IsNullOrEmpty(text) ? [] : Encoding.Unicode.GetBytes(text);
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle, int minimumNeedleLength)
    {
        if (haystack.Length == 0
            || needle.Length < minimumNeedleLength
            || needle.Length > haystack.Length)
        {
            return -1;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool matched = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return i;
        }

        return -1;
    }

    private static bool LooksLikeReadableString(string text)
    {
        bool hasVisible = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsSurrogate(ch))
                return false;
            if (char.IsControl(ch) && ch != '\t')
                return false;
            if (!char.IsWhiteSpace(ch))
                hasVisible = true;
        }

        return hasVisible;
    }

    private static bool IsCommittedMemory(IntPtr address)
    {
        try
        {
            return NativeInlineHookInterop.VirtualQuery(
                    address,
                    out NativeInlineHookInterop.MemoryBasicInformation info,
                    (uint)Marshal.SizeOf<NativeInlineHookInterop.MemoryBasicInformation>())
                != IntPtr.Zero
                && info.State == MemCommit;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class NativeImpTextIndependentSourceReport
{
    public int ScannedObjectBytes { get; set; }
    public int CandidateInlineWideOffset { get; set; } = -1;
    public int CurrentInlineWideOffset { get; set; } = -1;
    public int WideLikeSlotCount { get; set; }
    public int CurrentWideSlotCount { get; set; }
    public int CandidateWideSlotCount { get; set; }
    public int IndependentCandidateWideSlotCount { get; set; }
    public int CandidateCarrierByteSlotCount { get; set; }
    public string Decision { get; set; } = "diagnostic-only not-run";
    public List<NativeImpTextWideStringSlot> WideSlots { get; } = [];
    public List<NativeImpTextByteSlot> ByteSlots { get; } = [];
}

internal readonly record struct NativeImpTextWideStringSlot(
    int Offset,
    IntPtr Pointer,
    int Length,
    bool IsTruncated,
    bool IsCurrentTextPointerSlot,
    bool MatchesCurrent,
    bool MatchesCandidate,
    string Text);

internal readonly record struct NativeImpTextByteSlot(
    int Offset,
    IntPtr Pointer,
    int ScannedBytes,
    int CandidateCarrierIndex,
    bool IsCurrentTextPointerSlot,
    int CandidateCarrierLength);
#endif
