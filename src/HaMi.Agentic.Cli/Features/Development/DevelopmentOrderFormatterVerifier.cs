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

    // Revalidate copies at the next operation boundary. The hash commits to
    // the complete acceptance descriptor, not just a calculator file hash.
    internal static string RequireInputHash(DevelopmentOrderClaim claim, OrderS01Accepted accepted,
        OrderFormatterInputs inputs)
    {
        OrderCheck.Require(inputs.CalculatorAcceptance == accepted
            && inputs.Calculator.Length is > 0 and <= OrderCandidateCheckpoint.MaxBytes
            && inputs.FormatterBaseline.Length is > 0 and <= OrderCandidateCheckpoint.MaxBytes
            && DevelopmentJobState.Hash(inputs.FormatterBaseline) == claim.Binding.FormatterBaselineHash,
            "ORDER_FORMATTER_INPUT_BYTES");
        accepted.Candidate.ValidateBytes(inputs.Calculator);
        return OrderCandidateCheckpoint.FormatterInputHash(claim, accepted);
    }

    // Require the actual accepted S01 view, independently validated input
    // copies from RequireInputs, and the adjacent completed S02 model view.
    // The host must obtain those views from the journal, not synthesize them.
    internal static Dictionary<string, byte[]> Prepare(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger acceptedLedger, DevelopmentOrderOperationLedger completedModel,
        OrderFormatterInputs inputs)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var accepted = acceptedLedger.AcceptedS01 ?? throw new InvalidOperationException("ORDER_FORMATTER_PREDECESSOR");
        var observed = DevelopmentOrderModelReconciliation.Observe(owner, completedModel);
        if (observed.Status != OrderModelRecoveryStatus.Generated || observed.Reservation.Step != "S02"
            || observed.Candidate is not { Step: "S02", Quality: "Generated" } candidate
            || observed.Completion is not { } completion)
            throw new InvalidOperationException("ORDER_FORMATTER_VERIFIER_CANDIDATE");
        var limits = acceptedLedger.CurrentLimits ?? throw new InvalidOperationException("Order budget missing.");
        OrderCheck.Require(candidate.InputHash == RequireInputHash(claim, accepted, inputs)
            && completedModel == acceptedLedger.Reserve(claim.JobId, limits, observed.Reservation)
                .Checkpoint(claim, candidate).Complete(completion),
            "ORDER_FORMATTER_VERIFIER_HISTORY");
        byte[] calculator = owner.RequireOrderCandidate(accepted.Candidate);
        OrderCheck.Require(calculator.AsSpan().SequenceEqual(inputs.Calculator),
            "ORDER_FORMATTER_VERIFIER_PREDECESSOR");
        return new(StringComparer.Ordinal)
        {
            ["Demo.csproj"] = DevelopmentOrderCalculatorVerifier.ReadProjectAsset(),
            ["Program.cs"] = ReadAsset("Program"),
            ["PairFormatterVerifier.cs"] = ReadAsset("Checks"),
            ["PairCalculator.cs"] = calculator,
            ["PairFormatter.cs"] = owner.RequireOrderCandidate(candidate)
        };
    }

    internal static OrderOperationReservation ProposeGuest(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger acceptedLedger, DevelopmentOrderOperationLedger completedModel,
        OrderFormatterInputs inputs, int seconds, long now)
    {
        byte[] manifest = ExpectedManifest(owner, acceptedLedger, completedModel, inputs);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        return completedModel.Propose(claim.JobId, "S02", "guest", seconds, now, null,
            DevelopmentJobState.Hash(manifest));
    }

    // Same immutable manifest slot and directory checks as S01; no guest
    // dispatch or acceptance. The source must be exclusively host controlled.
    internal static void StoreGuestSnapshot(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger acceptedLedger, DevelopmentOrderOperationLedger completedModel,
        OrderFormatterInputs inputs, OrderOperationReservation proposal, string source)
    {
        byte[] manifest = RequireExpectedSnapshot(owner, acceptedLedger, completedModel, inputs, proposal, source);
        owner.StoreOrderSnapshotManifest(proposal, manifest);
        RequireGuestSnapshot(owner, acceptedLedger, completedModel, inputs, proposal, source);
    }

    internal static void RequireGuestSnapshot(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger acceptedLedger, DevelopmentOrderOperationLedger completedModel,
        OrderFormatterInputs inputs, OrderOperationReservation proposal, string source)
    {
        byte[] manifest = RequireExpectedSnapshot(owner, acceptedLedger, completedModel, inputs, proposal, source);
        OrderCheck.Require(owner.RequireOrderSnapshotManifest(proposal).AsSpan().SequenceEqual(manifest),
            "ORDER_FORMATTER_VERIFIER_MANIFEST");
    }

    private static byte[] ExpectedManifest(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger acceptedLedger, DevelopmentOrderOperationLedger completedModel,
        OrderFormatterInputs inputs) =>
        DevelopmentOrderSnapshot.CaptureBytes(Prepare(owner, acceptedLedger, completedModel, inputs), "Demo.csproj");

    private static byte[] RequireExpectedSnapshot(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger acceptedLedger, DevelopmentOrderOperationLedger completedModel,
        OrderFormatterInputs inputs, OrderOperationReservation proposal, string source)
    {
        byte[] manifest = ExpectedManifest(owner, acceptedLedger, completedModel, inputs);
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        OrderCheck.Require(proposal == completedModel.Propose(claim.JobId, "S02", "guest",
            proposal.ReservedSeconds, proposal.ObservedUtcTicks, null, DevelopmentJobState.Hash(manifest)),
            "ORDER_FORMATTER_VERIFIER_RESERVATION");
        OrderCheck.Require(DevelopmentOrderSnapshot.CaptureBytes(source, "Demo.csproj").AsSpan().SequenceEqual(manifest),
            "ORDER_FORMATTER_VERIFIER_SNAPSHOT");
        return manifest;
    }

    // Read-only historical correlation of genuine replay views. Neither
    // a protected report alone nor a fresh absence observation may replace
    // the journalled guest completion. No source directory is needed here.
    internal static OrderCandidateCheckpoint RequireCompletedGuest(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger acceptedLedger, DevelopmentOrderOperationLedger completedModel,
        DevelopmentOrderOperationLedger completedGuest, OrderFormatterInputs inputs,
        OrderGuestOutcome? independentlyObserved)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var outcome = completedGuest.LatestGuestOutcome
            ?? throw new InvalidOperationException("ORDER_FORMATTER_VERIFIER_OUTCOME");
        var reservation = outcome.Reservation;
        byte[] manifest = ExpectedManifest(owner, acceptedLedger, completedModel, inputs);
        var limits = completedModel.CurrentLimits ?? throw new InvalidOperationException("Order budget missing.");
        OrderCheck.Require(reservation == completedModel.Propose(claim.JobId, "S02", "guest",
            reservation.ReservedSeconds, reservation.ObservedUtcTicks, null, DevelopmentJobState.Hash(manifest))
            && completedGuest == completedModel.Reserve(claim.JobId, limits, reservation).CompleteGuest(outcome),
            "ORDER_FORMATTER_VERIFIER_GUEST_HISTORY");
        OrderCheck.Require(owner.RequireOrderSnapshotManifest(reservation).AsSpan().SequenceEqual(manifest),
            "ORDER_FORMATTER_VERIFIER_MANIFEST");
        byte[] report = owner.RequireOrderGuestReport(outcome, reservation);
        OrderCheck.Require(DevelopmentOrderGuestReconciliation.IsFormatterReport(reservation, report),
            "ORDER_FORMATTER_VERIFIER_REPORT");
        DevelopmentOrderGuestReconciliation.RequireObserved(outcome, independentlyObserved);
        return completedModel.LatestCandidate
            ?? throw new InvalidOperationException("ORDER_FORMATTER_VERIFIER_CANDIDATE");
    }

    // Independent bounded two-file scope check against the approved Git
    // baseline and both protected candidate descriptors. The observed stage
    // must contain exactly the accepted calculator and the new formatter.
    // The host must establish trusted checkout/stage ownership separately.
    internal static void RequirePairScope(DevelopmentJobStateStore.Lease owner, OrderS01Accepted accepted,
        OrderCandidateCheckpoint formatter, OrderGitBaseline baseline, IReadOnlyDictionary<string, byte[]> stagedPair)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        baseline.RequireBinding(claim.Binding);
        OrderCheck.Require(accepted.ArchiveHash == claim.ArchiveHash
            && accepted.Candidate is { Step: "S01", Quality: "Generated" }
            && accepted.ScopeHash == claim.Binding.ScopeHash
            && accepted.FormatterBaselineHash == claim.Binding.FormatterBaselineHash
            && formatter is { Step: "S02", Quality: "Generated" },
            "ORDER_FORMATTER_ACCEPTANCE_BINDING");
        formatter.RequireFormatterInput(claim, accepted);
        var scope = DevelopmentContract.Parse(new System.Text.UTF8Encoding(false, true).GetString(baseline.Scope));
        OrderCheck.Require(scope.TaskId == "sandbox-pair"
            && scope.AllowedWritePaths.SequenceEqual(
                [DevelopmentAgentContract.PairFirst, DevelopmentAgentContract.PairSecond], StringComparer.Ordinal)
            && stagedPair.Count == 2 && stagedPair.Keys.OrderBy(p => p, StringComparer.Ordinal)
                .SequenceEqual(scope.AllowedWritePaths.OrderBy(p => p, StringComparer.Ordinal), StringComparer.Ordinal),
            "ORDER_FORMATTER_ACCEPTANCE_SCOPE");
        byte[] calculatorBytes = owner.RequireOrderCandidate(accepted.Candidate);
        byte[] formatterBytes = owner.RequireOrderCandidate(formatter);
        OrderCheck.Require(!calculatorBytes.AsSpan().SequenceEqual(baseline.Calculator)
            && !formatterBytes.AsSpan().SequenceEqual(baseline.Formatter)
            && stagedPair[DevelopmentAgentContract.PairFirst].AsSpan().SequenceEqual(calculatorBytes)
            && stagedPair[DevelopmentAgentContract.PairSecond].AsSpan().SequenceEqual(formatterBytes),
            "ORDER_FORMATTER_ACCEPTANCE_BYTES");
    }

    // Proposal only: independently verify the completed guest and the exact
    // staged pair before constructing a journal acceptance descriptor.
    internal static OrderS02Accepted ProposeAcceptance(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger acceptedLedger, DevelopmentOrderOperationLedger completedModel,
        DevelopmentOrderOperationLedger completedGuest, OrderFormatterInputs inputs,
        OrderGuestOutcome? independentlyObserved, OrderGitBaseline baseline,
        IReadOnlyDictionary<string, byte[]> stagedPair, long now)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var predecessor = acceptedLedger.AcceptedS01 ?? throw new InvalidOperationException("ORDER_FORMATTER_PREDECESSOR");
        var candidate = RequireCompletedGuest(owner, acceptedLedger, completedModel, completedGuest, inputs, independentlyObserved);
        RequirePairScope(owner, predecessor, candidate, baseline, stagedPair);
        var guest = completedGuest.LatestGuestOutcome ?? throw new InvalidOperationException("ORDER_S02_GUEST_REQUIRED");
        var proposal = new OrderS02Accepted(1, claim.ArchiveHash, predecessor, candidate,
            guest.Reservation.OperationId, guest.ReportHash, claim.Binding.ScopeHash, now);
        OrderCheck.Require(independentlyObserved is not null && now >= independentlyObserved.ObservedUtcTicks,
            "ORDER_FORMATTER_ACCEPTANCE_CLOCK");
        _ = completedGuest.AcceptS02(claim, proposal);
        return proposal;
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
