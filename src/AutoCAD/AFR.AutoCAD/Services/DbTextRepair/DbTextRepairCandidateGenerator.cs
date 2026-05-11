using System;
using System.Text;
using System.Threading;

namespace AFR.Services.DbTextRepair;

internal static class DbTextRepairCandidateGenerator
{
#if !NETFRAMEWORK
    private static int _providerRegistered;
#endif

    public static bool TryGenerateBig5CarrierToGbkCandidate(string currentText, out string candidate, out string reason)
    {
        candidate = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrEmpty(currentText))
        {
            reason = "empty";
            return false;
        }

        try
        {
            EnsureCodePages();
            Encoding big5 = Encoding.GetEncoding(
                950,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            Encoding gbk = Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);

            byte[] carrierBytes = big5.GetBytes(currentText);
            string decoded = gbk.GetString(carrierBytes);
            if (string.IsNullOrEmpty(decoded) || string.Equals(decoded, currentText, StringComparison.Ordinal))
            {
                reason = "same";
                return false;
            }

            candidate = decoded;
            reason = "big5-carrier-to-gbk";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }
    }

    private static void EnsureCodePages()
    {
#if !NETFRAMEWORK
        if (Interlocked.Exchange(ref _providerRegistered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
    }
}
