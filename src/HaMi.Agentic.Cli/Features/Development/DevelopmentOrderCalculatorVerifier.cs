using HaMi.Agentic.Verification;

namespace HaMi.Agentic.Cli.Features.Development;

// Read-only preparation of the fixed S01 guest input. No file writes,
// dispatch, verification evidence or step acceptance. The caller must
// supply the actual journal-derived ledger under the exclusive issue lease.
internal static class DevelopmentOrderCalculatorVerifier
{
    internal const string Id = "pair-calculator";
    internal const string ExpectedStdout = "HaMi calculator acceptance passed";

    internal static Dictionary<string, byte[]> Prepare(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger recovered)
    {
        var observation = DevelopmentOrderModelReconciliation.Observe(owner, recovered);
        if (observation.Status != OrderModelRecoveryStatus.Generated
            || observation.Candidate is not { Quality: "Generated" } candidate)
            throw new InvalidOperationException("ORDER_CALCULATOR_VERIFIER_CANDIDATE");

        return new(StringComparer.Ordinal)
        {
            ["Demo.csproj"] = ReadAsset("Project"),
            ["Program.cs"] = ReadAsset("Program"),
            ["PairCalculatorVerifier.cs"] = ReadAsset("Checks"),
            ["PairCalculator.cs"] = owner.RequireOrderCandidate(candidate)
        };
    }

    private static byte[] ReadAsset(string name)
    {
        using Stream stream = typeof(DevelopmentOrderCalculatorVerifier).Assembly.GetManifestResourceStream(
            "HaMi.DevelopmentOrder.CalculatorVerifier." + name)
            ?? throw new InvalidOperationException("ORDER_CALCULATOR_VERIFIER_ASSET");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static void SelfTest()
    {
        int calls = 0;
        PairCalculatorVerifier.Verify((value, min, max) =>
        {
            calls++;
            return value < min ? min : value > max ? max : value;
        });
        if (calls != 1456)
            throw new InvalidOperationException("Calculator verifier coverage changed.");

        Reject((value, _, _) => value);
        Reject((_, min, _) => min);
        Reject((_, _, max) => max);
        Reject((value, min, max) => min == max ? unchecked(min + 1) : Math.Clamp(value, min, max));
        Reject((value, min, max) => value == int.MinValue ? int.MaxValue : Math.Clamp(value, min, max));
        Reject((_, _, _) => throw new InvalidOperationException("Calculator failed a reviewed boundary case."));

        if (ReadAsset("Project").Length == 0 || ReadAsset("Program").Length == 0 || ReadAsset("Checks").Length == 0)
            throw new InvalidOperationException("Calculator verifier assets missing.");
        Console.WriteLine("PASS: trusted calculator verifier checks inclusive bounds, equal bounds and integer extremes; incorrect fixtures rejected.");
    }

    private static void Reject(Func<int, int, int, int> clamp)
    {
        try
        {
            PairCalculatorVerifier.Verify(clamp);
        }
        catch (InvalidOperationException ex) when (ex.Message == "Calculator failed a reviewed boundary case.")
        {
            return;
        }

        throw new InvalidOperationException("Invalid calculator passed independent checks.");
    }
}
