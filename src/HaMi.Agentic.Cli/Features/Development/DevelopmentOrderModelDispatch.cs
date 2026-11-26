using System.Text;

namespace HaMi.Agentic.Cli.Features.Development;

internal sealed record OrderModelDispatchResult(OrderCandidateCheckpoint? Candidate, OrderOperationCompletion Completion);

// S01 only: a trusted host supplies a verified assignment and a single
// tool-free source-extraction transport. This is not a provider factory,
// a retry policy, a verifier or a journal writer. Never invoke during replay.
internal static class DevelopmentOrderModelDispatch
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // Real composition fixes the retry-disabled provider factory. The
    // injected core below is used only by the controlled offline probe.
    internal static async Task<OrderModelDispatchResult> RunS01Async(
        DevelopmentJobStateStore.Lease owner, DevelopmentOrderOperationLedger acknowledged,
        OrderOperationReservation reservation, DevelopmentOrderApproval approval,
        OrderAssignment assignment, IReadOnlyList<OrderAgentProfile> catalog, byte[] baseline,
        string apiKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var client = DevelopmentAiGenerator.CreateReservedClient(apiKey);
        var generator = new DevelopmentAiGenerator(client, assignment.Agent.Model);
        return await RunS01CoreAsync(owner, acknowledged, reservation, approval, assignment, catalog,
            baseline, generator, cancellationToken);
    }

    internal static Task<OrderModelDispatchResult> RunS01CoreAsync(
        DevelopmentJobStateStore.Lease owner, DevelopmentOrderOperationLedger acknowledged,
        OrderOperationReservation reservation, DevelopmentOrderApproval approval,
        OrderAssignment assignment, IReadOnlyList<OrderAgentProfile> catalog, byte[] baseline,
        DevelopmentAiGenerator generator, CancellationToken cancellationToken = default)
    {
        OrderCheck.Require(assignment.Agent.Provider == "openai"
            && assignment.Agent.Model == generator.ModelId
            && assignment.Agent.ConfigurationHash == generator.S01ConfigurationHash,
            "ORDER_MODEL_CONFIGURATION");
        return RunAsync(owner, acknowledged, reservation, approval, assignment, catalog,
            baseline, generator.GenerateS01Async, cancellationToken);
    }

    internal static async Task<OrderModelDispatchResult> RunAsync(
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
        string generated;
        try
        {
            generated = await generate(source, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (DevelopmentAiGenerator.S01TerminalResponseRejectedException)
        {
            // This catch covers only the trusted response adapter, not
            // preflight, storage, generic transport or verifier failures.
            _ = deadline.Limit(TimeSpan.FromSeconds(1));
            var failed = new OrderOperationEvidence(2, claim.ArchiveHash, reservation,
                OrderOperationEvidence.RejectedResponseObserver, "failed", claim.Binding.CalculatorBaselineHash,
                claim.Binding.CalculatorBaselineHash, null, DateTime.UtcNow.Ticks);
            var failedCompletion = failed.Completion();
            _ = acknowledged.Complete(failedCompletion);
            OrderCheck.Require(owner.StoreOrderOperationEvidence(failed) == failedCompletion,
                "ORDER_MODEL_EVIDENCE_COMPLETION");
            _ = deadline.Limit(TimeSpan.FromSeconds(1));
            return new(null, failedCompletion);
        }

        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        OrderCheck.Require(!string.IsNullOrWhiteSpace(generated) && generated.Length <= OrderCandidateCheckpoint.MaxBytes,
            "ORDER_MODEL_SOURCE_SIZE");
        byte[] bytes = Utf8.GetBytes(generated);
        _ = DecodeSource(bytes);
        var checkpoint = new OrderCandidateCheckpoint(1, claim.ArchiveHash, reservation.OperationId, "S01",
            DevelopmentAgentContract.PairFirst, claim.Binding.CalculatorBaselineHash,
            claim.Binding.CalculatorBaselineHash, bytes.Length, DevelopmentJobState.Hash(bytes), "Generated",
            DateTime.UtcNow.Ticks);
        var checkpointed = acknowledged.Checkpoint(claim, checkpoint);
        owner.RequireOrderModelDispatch(reservation);
        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        owner.StoreOrderCandidate(checkpoint, bytes);
        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        var evidence = new OrderOperationEvidence(2, claim.ArchiveHash, reservation,
            OrderOperationEvidence.GeneratedObserver, "succeeded", checkpoint.BaselineHash,
            checkpoint.InputHash, checkpoint.ArtifactHash, DateTime.UtcNow.Ticks);
        var completion = evidence.Completion();
        _ = checkpointed.Complete(completion);
        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        OrderCheck.Require(owner.StoreOrderOperationEvidence(evidence, checkpoint) == completion,
            "ORDER_MODEL_EVIDENCE_COMPLETION");
        _ = deadline.Limit(TimeSpan.FromSeconds(1));
        // Caller must persist checkpoint then completion under the same
        // lease. A crash before those acknowledgements is still pending.
        return new(checkpoint, completion);
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
