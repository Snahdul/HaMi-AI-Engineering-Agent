namespace HaMi.Agentic.Verification;

// Trusted fixture code. The same source is compiled for the offline tests
// and copied into the isolated S01 verification project.
internal static class PairCalculatorVerifier
{
    internal static void Verify(Func<int, int, int, int> clamp)
    {
        for (int min = -4; min <= 3; min++)
            for (int max = min; max <= 6; max++)
                for (int value = -9; value <= 11; value++)
                    Require(clamp, value, min, max);

        int[] extremes = [int.MinValue, int.MinValue + 1, -1, 0, 1, int.MaxValue - 1, int.MaxValue];
        foreach (int min in extremes)
            foreach (int max in extremes)
            {
                if (min > max)
                    continue;
                foreach (int value in extremes)
                    Require(clamp, value, min, max);
            }
    }

    private static void Require(Func<int, int, int, int> clamp, int value, int min, int max)
    {
        if (clamp(value, min, max) != Math.Clamp(value, min, max))
            throw new InvalidOperationException("Calculator failed a reviewed boundary case.");
    }
}
