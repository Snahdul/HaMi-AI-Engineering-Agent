using System.Text;

namespace HaMi.Agentic.Cli.Features.Development;

// S01 only: a trusted host supplies a verified assignment and a single
// tool-free source-extraction transport. This is not a provider factory,
// a retry policy, a verifier or a journal writer. Never invoke during replay.
internal static class DevelopmentOrderModelDispatch
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static async Task<OrderCandidateCheckpoint> RunAsync(
        DevelopmentJobStateStore.Lease owner, DevelopmentOrderOperationLedger acknowledged,
        OrderOperationReservation reservation, DevelopmentOrderApproval approval,
        OrderAssignment assignment, IReadOnlyList<OrderAgentProfile> catalog, byte[] baseline,
        Func<string, CancellationToken, Task<string>> generate,
        CancellationToken cancellationToken = default)
    {
        using var deadline = DevelopmentOrderDispatchDeadline.Start(acknowledged, reservation, cancellationToken);
        var archive = owner.RequireArchivedOrder();
        DevelopmentOrderClaim claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        OrderCheck.Require(reservation.Step == "S01" && reservation.Kind == "model"
            && reservation.SnapshotHash is null && acknowledged.ArchiveHash == claim.ArchiveHash
            && acknowledged.PolicyHash == claim.Binding.PolicyHash
            && approval.Binding == claim.Binding && approval.Order.Plan == archive.Plan,
            "ORDER_MODEL_DISPATCH_BINDING");
        DevelopmentOrderHandover.ValidateAssignment(approval, Guid.ParseExact(claim.JobId, "N"), assignment, catalog);
        OrderCheck.Require(assignment.StepId == reservation.Step, "ORDER_MODEL_DISPATCH_ASSIGNMENT");
        (reservation.Assignment ?? throw new InvalidOperationException("ORDER_OPERATION_ASSIGNMENT")).RequireAssignment(assignment);

        // Copy caller input before checking and decoding. Never give the
        // model access to the repository, other files or private storage.
        OrderCheck.Require(baseline.Length is > 0 and <= OrderCandidateCheckpoint.MaxBytes,
            "ORDER_MODEL_BASELINE_SIZE");
        byte[] input = baseline.ToArray();
        OrderCheck.Require(DevelopmentJobState.Hash(input) == claim.Binding.CalculatorBaselineHash,
            "ORDER_MODEL_BASELINE_HASH");
        string source = DecodeSource(input);
        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        owner.ConsumeOrderModelDispatch(acknowledged, reservation);
        _ = deadline.Limit(TimeSpan.FromSeconds(1));

        // The adapter must be asynchronous and honour cancellation. A
        // provider may ignore cancellation or complete remotely afterwards;
        // never turn that ambiguity into a retryable failure.
        string generated = await generate(source, deadline.Token).WaitAsync(deadline.Token);
        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        OrderCheck.Require(!string.IsNullOrWhiteSpace(generated) && generated.Length <= OrderCandidateCheckpoint.MaxBytes,
            "ORDER_MODEL_SOURCE_SIZE");
        byte[] bytes = Utf8.GetBytes(generated);
        _ = DecodeSource(bytes);
        var checkpoint = new OrderCandidateCheckpoint(1, claim.ArchiveHash, reservation.OperationId, "S01",
            DevelopmentAgentContract.PairFirst, claim.Binding.CalculatorBaselineHash,
            claim.Binding.CalculatorBaselineHash, bytes.Length, DevelopmentJobState.Hash(bytes), "Generated",
            DateTime.UtcNow.Ticks);
        _ = acknowledged.Checkpoint(claim, checkpoint);
        owner.RequireOrderModelDispatch(reservation);
        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        owner.StoreOrderCandidate(checkpoint, bytes);
        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        return checkpoint;
    }

    private static string DecodeSource(byte[] bytes)
    {
        OrderCheck.Require(bytes.Length is > 0 and <= OrderCandidateCheckpoint.MaxBytes
            && !bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "ORDER_MODEL_SOURCE_SIZE");
        string source = Utf8.GetString(bytes);
        OrderCheck.Require(!string.IsNullOrWhiteSpace(source)
            && !source.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')),
            "ORDER_MODEL_SOURCE_TEXT");
        return source;
    }
}
