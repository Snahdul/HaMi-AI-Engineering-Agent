namespace HaMi.Agentic.Cli.Features.Development;

// Genuine historical replay views; the host must supply these under the
// exclusive issue lease, not synthesize them from private files.
internal sealed record OrderPairHistory(DevelopmentOrderOperationLedger CalculatorModel,
    DevelopmentOrderOperationLedger CalculatorGuest, DevelopmentOrderOperationLedger AcceptedCalculator,
    DevelopmentOrderOperationLedger FormatterModel, DevelopmentOrderOperationLedger FormatterGuest,
    DevelopmentOrderOperationLedger AcceptedPair);

// Independent host observations, not generated test data or authority to
// dispatch. Both staging views must be exclusively host controlled.
internal sealed record OrderPairObservation(OrderGuestOutcome? Calculator, OrderGuestOutcome? Formatter,
    OrderGitBaseline Baseline, IReadOnlyDictionary<string, byte[]> CalculatorStage,
    IReadOnlyDictionary<string, byte[]> PairStage);

internal static class DevelopmentOrderIntegrationVerifier
{
    internal const string Id = "pair-integration";
    internal const string ExpectedStdout = "HaMi pair acceptance passed";

    // Read-only preparation of the original integration harness and both
    // accepted candidates. Verify the entire chain before constructing bytes.
    internal static Dictionary<string, byte[]> Prepare(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var accepted = history.AcceptedPair.AcceptedS02
            ?? throw new InvalidOperationException("ORDER_FINAL_ACCEPTANCE_REQUIRED");
        OrderCheck.Require(history.AcceptedPair == history.FormatterGuest.AcceptS02(claim, accepted)
            && history.AcceptedPair.AcceptedS01 == accepted.Predecessor
            && history.AcceptedCalculator.AcceptedS01 == accepted.Predecessor,
            "ORDER_FINAL_ACCEPTANCE_HISTORY");
        var inputs = DevelopmentOrderFormatterVerifier.RequireInputs(owner, history.CalculatorModel,
            history.CalculatorGuest, history.AcceptedCalculator, observed.Calculator, observed.Baseline, observed.CalculatorStage);
        var formatter = DevelopmentOrderFormatterVerifier.RequireCompletedGuest(owner, history.AcceptedCalculator,
            history.FormatterModel, history.FormatterGuest, inputs, observed.Formatter);
        OrderCheck.Require(formatter == accepted.Candidate && inputs.CalculatorAcceptance == accepted.Predecessor,
            "ORDER_FINAL_ACCEPTANCE_CANDIDATES");
        DevelopmentOrderFormatterVerifier.RequirePairScope(owner, accepted.Predecessor, formatter,
            observed.Baseline, observed.PairStage);
        return new(StringComparer.Ordinal)
        {
            ["Demo.csproj"] = DevelopmentOrderCalculatorVerifier.ReadProjectAsset(),
            ["Program.cs"] = ReadProgram(),
            ["PairCalculator.cs"] = owner.RequireOrderCandidate(accepted.Predecessor.Candidate),
            ["PairFormatter.cs"] = owner.RequireOrderCandidate(formatter)
        };
    }

    internal static OrderOperationReservation ProposeGuest(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed, int seconds, long now)
    {
        byte[] manifest = ExpectedManifest(owner, history, observed);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        OrderCheck.Require(seconds >= history.AcceptedPair.FinalReserveSeconds, "ORDER_FINAL_TIME_TOO_SHORT");
        return history.AcceptedPair.Propose(claim.JobId, "FINAL", "guest", seconds, now, null,
            DevelopmentJobState.Hash(manifest));
    }

    internal static void StoreGuestSnapshot(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed, OrderOperationReservation proposal, string source)
    {
        byte[] manifest = RequireExpectedSnapshot(owner, history, observed, proposal, source);
        owner.StoreOrderSnapshotManifest(proposal, manifest);
        RequireGuestSnapshot(owner, history, observed, proposal, source);
    }

    internal static void RequireGuestSnapshot(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed, OrderOperationReservation proposal, string source)
    {
        byte[] manifest = RequireExpectedSnapshot(owner, history, observed, proposal, source);
        OrderCheck.Require(owner.RequireOrderSnapshotManifest(proposal).AsSpan().SequenceEqual(manifest),
            "ORDER_FINAL_VERIFIER_MANIFEST");
    }

    private static byte[] ExpectedManifest(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed) =>
        DevelopmentOrderSnapshot.CaptureBytes(Prepare(owner, history, observed), "Demo.csproj");

    private static byte[] RequireExpectedSnapshot(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed, OrderOperationReservation proposal, string source)
    {
        byte[] manifest = ExpectedManifest(owner, history, observed);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        OrderCheck.Require(proposal == history.AcceptedPair.Propose(claim.JobId, "FINAL", "guest",
            proposal.ReservedSeconds, proposal.ObservedUtcTicks, null, DevelopmentJobState.Hash(manifest))
            && proposal.ReservedSeconds >= history.AcceptedPair.FinalReserveSeconds, "ORDER_FINAL_VERIFIER_RESERVATION");
        OrderCheck.Require(DevelopmentOrderSnapshot.CaptureBytes(source, "Demo.csproj").AsSpan().SequenceEqual(manifest),
            "ORDER_FINAL_VERIFIER_SNAPSHOT");
        return manifest;
    }

    // Reconstruct from genuine adjacent replay views and protected bytes.
    // No dispatch, journal append or terminal order acceptance here.
    internal static OrderGuestOutcome RequireCompletedGuest(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed, DevelopmentOrderOperationLedger completedGuest,
        OrderGuestOutcome? independentlyObserved)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var outcome = completedGuest.LatestGuestOutcome
            ?? throw new InvalidOperationException("ORDER_FINAL_VERIFIER_OUTCOME");
        var reservation = outcome.Reservation;
        byte[] manifest = ExpectedManifest(owner, history, observed);
        var limits = history.AcceptedPair.CurrentLimits ?? throw new InvalidOperationException("Order budget missing.");
        OrderCheck.Require(reservation == history.AcceptedPair.Propose(claim.JobId, "FINAL", "guest",
            reservation.ReservedSeconds, reservation.ObservedUtcTicks, null, DevelopmentJobState.Hash(manifest))
            && completedGuest == history.AcceptedPair.ReserveWithRequiredWork(claim.JobId, limits, reservation, [])
                .CompleteGuest(outcome), "ORDER_FINAL_VERIFIER_HISTORY");
        OrderCheck.Require(owner.RequireOrderSnapshotManifest(reservation).AsSpan().SequenceEqual(manifest),
            "ORDER_FINAL_VERIFIER_MANIFEST");
        byte[] report = owner.RequireOrderGuestReport(outcome, reservation);
        OrderCheck.Require(DevelopmentOrderGuestReconciliation.IsIntegrationReport(reservation, report),
            "ORDER_FINAL_VERIFIER_REPORT");
        DevelopmentOrderGuestReconciliation.RequireObserved(outcome, independentlyObserved);
        return outcome;
    }

    // Proposal only, not a terminal journal write or publication authority.
    internal static OrderAccepted ProposeAcceptance(DevelopmentJobStateStore.Lease owner,
        OrderPairHistory history, OrderPairObservation observed, DevelopmentOrderOperationLedger completedGuest,
        OrderGuestOutcome? independentlyObserved, long now)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var pair = history.AcceptedPair.AcceptedS02 ?? throw new InvalidOperationException("ORDER_FINAL_ACCEPTANCE_REQUIRED");
        var guest = RequireCompletedGuest(owner, history, observed, completedGuest, independentlyObserved);
        var proposal = new OrderAccepted(1, claim.ArchiveHash, DevelopmentOrderOperationLedger.PairAcceptanceHash(pair),
            guest.Reservation.OperationId, guest.ReportHash,
            guest.Reservation.SnapshotHash ?? throw new InvalidOperationException("ORDER_FINAL_VERIFIER_MANIFEST"), now);
        OrderCheck.Require(independentlyObserved is not null && now >= independentlyObserved.ObservedUtcTicks
            && observed.Calculator is not null && now >= observed.Calculator.ObservedUtcTicks
            && observed.Formatter is not null && now >= observed.Formatter.ObservedUtcTicks, "ORDER_FINAL_ACCEPTANCE_CLOCK");
        _ = completedGuest.AcceptOrder(claim, proposal);
        return proposal;
    }

    internal static bool MatchesOutput(string output, string error) =>
        error.Length == 0 && (output == ExpectedStdout + "\n" || output == ExpectedStdout + "\r\n");

    internal static byte[] ReadProgram()
    {
        using Stream stream = typeof(DevelopmentOrderIntegrationVerifier).Assembly.GetManifestResourceStream(
            "HaMi.DevelopmentOrder.IntegrationVerifier.Program")
            ?? throw new InvalidOperationException("ORDER_FINAL_VERIFIER_ASSET");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
