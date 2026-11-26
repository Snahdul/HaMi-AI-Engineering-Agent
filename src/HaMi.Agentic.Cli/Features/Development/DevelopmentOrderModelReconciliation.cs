using System.Globalization;

namespace HaMi.Agentic.Cli.Features.Development;

internal enum OrderModelRecoveryStatus
{
    PendingBlocked,
    Generated,
    Rejected
}

internal sealed record OrderModelRecoveryObservation(OrderModelRecoveryStatus Status,
    OrderOperationReservation Reservation, OrderCandidateCheckpoint? Candidate, OrderOperationCompletion? Completion);

// Read-only last S01 model-operation recovery. The caller must supply a
// genuinely replayed journal ledger under the exclusive issue lease. No
// local file can supply a missing journal event or authorize another call.
internal static class DevelopmentOrderModelReconciliation
{
    internal static OrderModelRecoveryObservation Observe(DevelopmentJobStateStore.Lease owner,
        DevelopmentOrderOperationLedger recovered)
    {
        DevelopmentOrderClaim claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        _ = owner.RequireArchivedOrder();
        OrderCheck.Require(recovered.ArchiveHash == claim.ArchiveHash
            && recovered.PolicyHash == claim.Binding.PolicyHash
            && recovered.Sequence is > 0 and <= OrderCandidateCheckpoint.MaxSequence,
            "ORDER_MODEL_RECOVERY_BINDING");
        string operationId = claim.JobId + ":" + recovered.Sequence.ToString(CultureInfo.InvariantCulture);
        var reservation = recovered.Pending ?? recovered.LatestModelReservation
            ?? throw new InvalidOperationException("ORDER_MODEL_RECOVERY_RESERVATION");
        OrderCheck.Require(reservation.OperationId == operationId && reservation.Kind == "model"
            && reservation.Step == "S01", "ORDER_MODEL_RECOVERY_RESERVATION");

        // A previous attempt's candidate must never become this attempt's
        // candidate. A matching journal checkpoint is integrity-checked,
        // but is not an outcome and is not returned for further execution.
        var candidate = recovered.LatestCandidate?.OperationId == operationId ? recovered.LatestCandidate : null;
        if (recovered.Pending is not null)
        {
            if (candidate is not null)
            {
                owner.RequireOrderModelDispatch(reservation);
                _ = owner.RequireOrderCandidate(candidate);
            }

            return new(OrderModelRecoveryStatus.PendingBlocked, reservation, null, null);
        }

        // Completion descriptor comes exclusively from journal replay, not
        // from the private evidence file or a caller-provided digest.
        var completion = recovered.LatestModelCompletion
            ?? throw new InvalidOperationException("ORDER_MODEL_RECOVERY_COMPLETION");
        OrderCheck.Require(completion.OperationId == operationId
            && completion.Outcome == recovered.LastOutcome
            && completion.EvidenceHash == recovered.LastEvidenceHash
            && completion.ObservedUtcTicks <= recovered.LastUtcTicks,
            "ORDER_MODEL_RECOVERY_COMPLETION");
        OrderOperationEvidence evidence = owner.RequireOrderOperationEvidence(reservation, completion, candidate);
        OrderModelRecoveryStatus status = (evidence.Observer, completion.Outcome) switch
        {
            (OrderOperationEvidence.GeneratedObserver, "succeeded") when candidate is not null => OrderModelRecoveryStatus.Generated,
            (OrderOperationEvidence.RejectedResponseObserver, "failed") when candidate is null => OrderModelRecoveryStatus.Rejected,
            _ => throw new InvalidOperationException("ORDER_MODEL_RECOVERY_OBSERVER")
        };
        return new(status, reservation, candidate, completion);
    }
}
