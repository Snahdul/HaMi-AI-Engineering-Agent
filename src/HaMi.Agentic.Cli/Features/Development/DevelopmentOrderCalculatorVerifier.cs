using System.Text;
using HaMi.Agentic.Verification;

namespace HaMi.Agentic.Cli.Features.Development;

// Preparation and exact manifest binding of the fixed S01 guest input.
// No dispatch, verification evidence or step acceptance. The caller must
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

    // Prepare a proposal, not a reservation acknowledgement. The caller
    // still has to admit mandatory remaining work and persist before dispatch.
    internal static OrderOperationReservation ProposeGuest(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completedModel, int seconds, long now)
    {
        byte[] manifest = ExpectedManifest(owner, completedModel);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        return completedModel.Propose(claim.JobId, "S01", "guest", seconds, now, null,
            DevelopmentJobState.Hash(manifest));
    }

    // Only the trusted host may materialize the prepared files into an
    // exclusive source directory. Verify exact content before writing the
    // existing immutable manifest slot, never bless arbitrary directory bytes.
    // Called after admission, before journal acknowledgement. No dispatch.
    internal static void StoreGuestSnapshot(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completedModel, OrderOperationReservation proposal, string source)
    {
        byte[] manifest = RequireExpectedSnapshot(owner, completedModel, proposal, source);
        owner.StoreOrderSnapshotManifest(proposal, manifest);
        RequireGuestSnapshot(owner, completedModel, proposal, source);
    }

    internal static void RequireGuestSnapshot(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completedModel, OrderOperationReservation proposal, string source)
    {
        byte[] expected = RequireExpectedSnapshot(owner, completedModel, proposal, source);
        OrderCheck.Require(owner.RequireOrderSnapshotManifest(proposal).AsSpan().SequenceEqual(expected),
            "ORDER_CALCULATOR_VERIFIER_MANIFEST");
    }

    private static byte[] ExpectedManifest(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completedModel) =>
        DevelopmentOrderSnapshot.CaptureBytes(Prepare(owner, completedModel), "Demo.csproj");

    private static byte[] RequireExpectedSnapshot(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completedModel, OrderOperationReservation proposal, string source)
    {
        byte[] manifest = ExpectedManifest(owner, completedModel);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        OrderCheck.Require(proposal == completedModel.Propose(claim.JobId, "S01", "guest",
            proposal.ReservedSeconds, proposal.ObservedUtcTicks, null, DevelopmentJobState.Hash(manifest)),
            "ORDER_CALCULATOR_VERIFIER_RESERVATION");
        byte[] actual = DevelopmentOrderSnapshot.CaptureBytes(source, "Demo.csproj");
        OrderCheck.Require(actual.AsSpan().SequenceEqual(manifest), "ORDER_CALCULATOR_VERIFIER_SNAPSHOT");
        return manifest;
    }

    // Read-only historical correlation, not an Accepted event. The trusted
    // host must supply genuine adjacent replay views and an independent fresh
    // absence observation under the exclusive lease. A local file cannot
    // settle a pending guest or substitute for its journalled outcome.
    internal static OrderCandidateCheckpoint RequireCompletedGuest(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completedModel, DevelopmentOrderOperationLedger completedGuest,
        OrderGuestOutcome? independentlyObserved)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var outcome = completedGuest.LatestGuestOutcome
            ?? throw new InvalidOperationException("ORDER_CALCULATOR_VERIFIER_OUTCOME");
        var reservation = outcome.Reservation;
        byte[] manifest = ExpectedManifest(owner, completedModel);
        var limits = completedModel.CurrentLimits ?? throw new InvalidOperationException("Order budget missing.");
        OrderCheck.Require(reservation == completedModel.Propose(claim.JobId, "S01", "guest",
            reservation.ReservedSeconds, reservation.ObservedUtcTicks, null, DevelopmentJobState.Hash(manifest))
            && completedGuest == completedModel.Reserve(claim.JobId, limits, reservation).CompleteGuest(outcome),
            "ORDER_CALCULATOR_VERIFIER_HISTORY");
        OrderCheck.Require(owner.RequireOrderSnapshotManifest(reservation).AsSpan().SequenceEqual(manifest),
            "ORDER_CALCULATOR_VERIFIER_MANIFEST");
        byte[] report = owner.RequireOrderGuestReport(outcome, reservation);
        OrderCheck.Require(DevelopmentOrderGuestReconciliation.IsCalculatorReport(reservation, report),
            "ORDER_CALCULATOR_VERIFIER_REPORT");
        DevelopmentOrderGuestReconciliation.RequireObserved(outcome, independentlyObserved);
        // Generated remains Generated; independent baseline/scope verification
        // and a journalled acceptance are separate gates.
        return completedModel.LatestCandidate
            ?? throw new InvalidOperationException("ORDER_CALCULATOR_VERIFIER_CANDIDATE");
    }

    // The host supplies independently observed, exact approved baseline
    // bytes and the complete two-file staged pair. No candidate code runs
    // here. This is bounded pair scope, not whole-checkout attestation.
    internal static OrderS01Accepted ProposeAcceptance(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger completedModel, DevelopmentOrderOperationLedger completedGuest,
        OrderGuestOutcome? independentlyObserved, string baseCommit, byte[] scopeBytes,
        byte[] calculatorBaseline, byte[] formatterBaseline, IReadOnlyDictionary<string, byte[]> stagedPair,
        long now)
    {
        var candidate = RequireCompletedGuest(owner, completedModel, completedGuest, independentlyObserved);
        RequirePairScope(owner, candidate, baseCommit, scopeBytes, calculatorBaseline, formatterBaseline, stagedPair);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var binding = claim.Binding;
        var guest = completedGuest.LatestGuestOutcome
            ?? throw new InvalidOperationException("ORDER_CALCULATOR_VERIFIER_OUTCOME");
        var proposal = new OrderS01Accepted(1, claim.ArchiveHash, candidate, guest.Reservation.OperationId,
            guest.ReportHash, binding.ScopeHash, binding.FormatterBaselineHash, now);
        OrderCheck.Require(independentlyObserved is not null && now >= independentlyObserved.ObservedUtcTicks,
            "ORDER_CALCULATOR_ACCEPTANCE_CLOCK");
        _ = completedGuest.AcceptS01(claim, proposal);
        return proposal;
    }

    // Reusable read-only scope check, including after an already accepted
    // step has outlived its original execution window. No new acceptance.
    internal static void RequirePairScope(DevelopmentJobStateStore.Lease owner, OrderCandidateCheckpoint candidate,
        string baseCommit, byte[] scopeBytes, byte[] calculatorBaseline, byte[] formatterBaseline,
        IReadOnlyDictionary<string, byte[]> stagedPair)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var binding = claim.Binding;
        OrderCheck.Require(baseCommit == binding.BaseCommit
            && DevelopmentJobState.Hash(scopeBytes) == binding.ScopeHash
            && DevelopmentJobState.Hash(calculatorBaseline) == binding.CalculatorBaselineHash
            && DevelopmentJobState.Hash(formatterBaseline) == binding.FormatterBaselineHash,
            "ORDER_CALCULATOR_ACCEPTANCE_BASELINE");
        var scope = DevelopmentContract.Parse(new UTF8Encoding(false, true).GetString(scopeBytes));
        OrderCheck.Require(scope.TaskId == "sandbox-pair"
            && scope.AllowedWritePaths.SequenceEqual(
                [DevelopmentAgentContract.PairFirst, DevelopmentAgentContract.PairSecond], StringComparer.Ordinal),
            "ORDER_CALCULATOR_ACCEPTANCE_SCOPE");
        // Enumerate the actual keys with ordinal semantics; a caller's
        // dictionary comparer must not widen the approved path set.
        OrderCheck.Require(stagedPair.Count == 2 && stagedPair.Keys.OrderBy(p => p, StringComparer.Ordinal)
            .SequenceEqual(scope.AllowedWritePaths.OrderBy(p => p, StringComparer.Ordinal), StringComparer.Ordinal),
            "ORDER_CALCULATOR_ACCEPTANCE_SCOPE");
        byte[] accepted = owner.RequireOrderCandidate(candidate);
        OrderCheck.Require(!accepted.AsSpan().SequenceEqual(calculatorBaseline)
            && stagedPair[DevelopmentAgentContract.PairFirst].AsSpan().SequenceEqual(accepted)
            && stagedPair[DevelopmentAgentContract.PairSecond].AsSpan().SequenceEqual(formatterBaseline),
            "ORDER_CALCULATOR_ACCEPTANCE_BYTES");
    }

    // Linux guest writes one exact line. Accept CRLF for transport portability,
    // but no trimming, extra lines, whitespace or stderr diagnostics.
    internal static bool MatchesOutput(string output, string error) =>
        error.Length == 0 && (output == ExpectedStdout + "\n" || output == ExpectedStdout + "\r\n");

    private static byte[] ReadAsset(string name)
    {
        using Stream stream = typeof(DevelopmentOrderCalculatorVerifier).Assembly.GetManifestResourceStream(
            "HaMi.DevelopmentOrder.CalculatorVerifier." + name)
            ?? throw new InvalidOperationException("ORDER_CALCULATOR_VERIFIER_ASSET: " + name);
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

        if (!MatchesOutput(ExpectedStdout + "\n", "") || !MatchesOutput(ExpectedStdout + "\r\n", "")
            || MatchesOutput(ExpectedStdout, "") || MatchesOutput(ExpectedStdout + "\n\n", "")
            || MatchesOutput(" " + ExpectedStdout + "\n", "") || MatchesOutput(ExpectedStdout + "\n", "diagnostic")
            || MatchesOutput("HaMi pair acceptance passed\n", ""))
            throw new InvalidOperationException("Calculator semantic output checks differ.");

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
