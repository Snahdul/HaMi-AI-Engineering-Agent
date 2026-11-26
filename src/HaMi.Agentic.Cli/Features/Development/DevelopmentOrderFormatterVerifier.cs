using System.Globalization;
using HaMi.Agentic.Verification;

namespace HaMi.Agentic.Cli.Features.Development;

// Read-only handover from the accepted S01 journal state. The candidate
// descriptor remains Generated; the separate acceptance event confers acceptance.
internal sealed record OrderFormatterInputs(OrderS01Accepted CalculatorAcceptance,
    byte[] Calculator, byte[] FormatterBaseline);

internal static class DevelopmentOrderFormatterVerifier
{
    internal const string Id = "pair-formatter";
    internal const string ExpectedStdout = "HaMi formatter acceptance passed";

    // Requires genuine adjacent replay views and a fresh trusted observation,
    // before any S02 operation. Not dispatch authority or a new journal event.
    internal static OrderFormatterInputs RequireInputs(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completedModel, DevelopmentOrderOperationLedger completedGuest,
        DevelopmentOrderOperationLedger acceptedLedger, OrderGuestOutcome? independentlyObserved,
        OrderGitBaseline baseline, IReadOnlyDictionary<string, byte[]> stagedPair)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var accepted = acceptedLedger.AcceptedS01 ?? throw new InvalidOperationException("ORDER_FORMATTER_PREDECESSOR");
        OrderCheck.Require(acceptedLedger == completedGuest.AcceptS01(claim, accepted),
            "ORDER_FORMATTER_ACCEPTED_HISTORY");
        var candidate = DevelopmentOrderCalculatorVerifier.RequireCompletedGuest(owner,
            completedModel, completedGuest, independentlyObserved);
        baseline.RequireBinding(claim.Binding);
        DevelopmentOrderCalculatorVerifier.RequirePairScope(owner, candidate, baseline.Commit,
            baseline.Scope, baseline.Calculator, baseline.Formatter, stagedPair);
        OrderCheck.Require(candidate == accepted.Candidate, "ORDER_FORMATTER_PREDECESSOR");
        return new(accepted, owner.RequireOrderCandidate(candidate), baseline.Formatter.ToArray());
    }

    internal static bool MatchesOutput(string output, string error) =>
        error.Length == 0 && (output == ExpectedStdout + "\n" || output == ExpectedStdout + "\r\n");

    internal static byte[] ReadAsset(string name)
    {
        if (name is not ("Program" or "Checks"))
            throw new InvalidOperationException("ORDER_FORMATTER_VERIFIER_ASSET");
        using Stream stream = typeof(DevelopmentOrderFormatterVerifier).Assembly.GetManifestResourceStream(
            "HaMi.DevelopmentOrder.FormatterVerifier." + name)
            ?? throw new InvalidOperationException("ORDER_FORMATTER_VERIFIER_ASSET: " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static void SelfTest()
    {
        static string Format(int value, int min, int max) => FormattableString.Invariant($"{value} [{min},{max}]");
        int calls = 0;
        PairFormatterVerifier.Verify((value, min, max) =>
        {
            calls++;
            return Format(value, min, max);
        });
        if (calls != 2912)
            throw new InvalidOperationException("Formatter verifier coverage changed.");
        Reject((value, min, max) => FormattableString.Invariant($"{value} [{max},{min}]"));
        Reject((value, min, max) => FormattableString.Invariant($"{value} [{min}, {max}]"));
        Reject((value, min, max) => string.Create(CultureInfo.CurrentCulture, $"{value} [{min},{max}]"));
        Reject((value, min, max) => value == int.MinValue ? Format(int.MaxValue, min, max) : Format(value, min, max));
        if (!MatchesOutput(ExpectedStdout + "\n", "") || !MatchesOutput(ExpectedStdout + "\r\n", "")
            || MatchesOutput(ExpectedStdout, "") || MatchesOutput(ExpectedStdout + "\n\n", "")
            || MatchesOutput(ExpectedStdout + "\n", "diagnostic")
            || MatchesOutput(DevelopmentOrderCalculatorVerifier.ExpectedStdout + "\n", ""))
            throw new InvalidOperationException("Formatter semantic output checks differ.");
        if (ReadAsset("Program").Length == 0 || ReadAsset("Checks").Length == 0)
            throw new InvalidOperationException("Formatter verifier assets missing.");
        Console.WriteLine("PASS: trusted formatter verifier checks exact invariant output in two cultures, inclusive ranges and integer extremes; incorrect fixtures rejected.");
    }

    private static void Reject(Func<int, int, int, string> format)
    {
        try
        {
            PairFormatterVerifier.Verify(format);
        }
        catch (InvalidOperationException ex) when (ex.Message == "Formatter failed a reviewed boundary case.")
        {
            return;
        }
        throw new InvalidOperationException("Invalid formatter passed independent checks.");
    }
}
