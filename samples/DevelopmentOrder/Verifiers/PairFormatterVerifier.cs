using System.Globalization;

namespace HaMi.Agentic.Verification;

// Trusted formatter-only harness. The same source is compiled for offline
// delegate tests and embedded for the future isolated verifier project.
internal static class PairFormatterVerifier
{
    internal static void Verify(Func<int, int, int, string> format)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        var alternate = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        alternate.NumberFormat.NegativeSign = "~";
        try
        {
            foreach (CultureInfo culture in new[] { CultureInfo.InvariantCulture, alternate })
            {
                CultureInfo.CurrentCulture = culture;
                for (int min = -4; min <= 3; min++)
                    for (int max = min; max <= 6; max++)
                        for (int value = -9; value <= 11; value++)
                            Require(format, Math.Clamp(value, min, max), min, max);

                int[] extremes = [int.MinValue, int.MinValue + 1, -1, 0, 1, int.MaxValue - 1, int.MaxValue];
                foreach (int min in extremes)
                    foreach (int max in extremes)
                    {
                        if (min > max)
                            continue;
                        foreach (int value in extremes)
                            Require(format, Math.Clamp(value, min, max), min, max);
                    }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void Require(Func<int, int, int, string> format, int value, int min, int max)
    {
        string expected = value.ToString(CultureInfo.InvariantCulture)
            + " [" + min.ToString(CultureInfo.InvariantCulture)
            + "," + max.ToString(CultureInfo.InvariantCulture) + "]";
        if (!string.Equals(format(value, min, max), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Formatter failed a reviewed boundary case.");
    }
}
