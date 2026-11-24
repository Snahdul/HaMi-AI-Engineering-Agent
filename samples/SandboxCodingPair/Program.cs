// TRUSTED: This harness is never writable by the candidate generator.
using HaMi.Agentic.Cli.Features.Development;
using System.Globalization;

for (int min = -4; min <= 3; min++)
{
    for (int max = min; max <= 6; max++)
    {
        for (int value = -9; value <= 11; value++)
        {
            int expected = Math.Clamp(value, min, max);
            int actual = PairCalculator.Clamp(value, min, max);
            if (actual != expected)
                throw new InvalidOperationException("Calculator failed a reviewed boundary case.");
            string label = PairFormatter.Format(actual, min, max);
            string required = expected.ToString(CultureInfo.InvariantCulture)
                + " [" + min.ToString(CultureInfo.InvariantCulture)
                + "," + max.ToString(CultureInfo.InvariantCulture) + "]";
            if (!string.Equals(label, required, StringComparison.Ordinal))
                throw new InvalidOperationException("Formatter failed a reviewed boundary case.");
        }
    }
}
Console.WriteLine("HaMi pair acceptance passed");
