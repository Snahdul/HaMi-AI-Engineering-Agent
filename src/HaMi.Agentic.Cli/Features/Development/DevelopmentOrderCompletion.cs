namespace HaMi.Agentic.Cli.Features.Development;

// Coordinate completion of already performed work. The guarded host must
// establish the outcome and persist it; no observation settles a pending
// reservation on replay and no callback may dispatch a model or guest.
internal static class DevelopmentOrderCompletion
{
    internal static OrderModelRecoveryObservation ObserveModel(
        DevelopmentJobStateStore.Lease owner, DevelopmentOrderOperationLedger ledger)
    {
        var claim = owner.CurrentOrder ?? throw new InvalidOperationException("Order claim missing.");
        var action = DevelopmentOrderWorkflow.Select(claim, ledger);
        var observation = DevelopmentOrderModelReconciliation.Observe(owner, ledger);
        var expected = observation.Status switch
        {
            OrderModelRecoveryStatus.PendingBlocked => OrderWorkflowAction.ReconcilePending,
            OrderModelRecoveryStatus.Rejected => OrderWorkflowAction.ReviewRejectedModel,
            OrderModelRecoveryStatus.Generated when observation.Reservation.Step == "S01" => OrderWorkflowAction.VerifyCalculator,
            OrderModelRecoveryStatus.Generated when observation.Reservation.Step == "S02" => OrderWorkflowAction.VerifyFormatter,
            _ => throw new InvalidOperationException("ORDER_COMPLETION_MODEL_STATUS")
        };
        OrderCheck.Require(action == expected, "ORDER_COMPLETION_MODEL_ACTION");
        return observation;
    }

    internal static async Task<DevelopmentOrderOperationLedger> RecordModelAsync(
        DevelopmentJobStateStore.Lease owner, DevelopmentOrderOperationLedger before,
        OrderOperationCompletion completion, Func<OrderOperationCompletion, Task<DevelopmentOrderOperationLedger>> persist)
    {
        OrderCheck.Require(ObserveModel(owner, before).Status == OrderModelRecoveryStatus.PendingBlocked,
            "ORDER_COMPLETION_MODEL_PENDING");
        var expected = before.Complete(completion);
        // Validate private evidence before asking the existing guarded host
        // to persist. The proposed fold is not a recovered journal view.
        var evidence = ObserveModel(owner, expected);
        var acknowledged = await persist(completion);
        OrderCheck.Require(acknowledged == expected && ObserveModel(owner, acknowledged) == evidence,
            "ORDER_COMPLETION_MODEL_ACK");
        return acknowledged;
    }

    internal static async Task<DevelopmentOrderOperationLedger> RecordGuestAsync(
        DevelopmentOrderClaim claim, DevelopmentOrderOperationLedger before,
        OrderGuestOutcome outcome, Func<OrderGuestOutcome, Task<DevelopmentOrderOperationLedger>> persist)
    {
        OrderCheck.Require(DevelopmentOrderWorkflow.Select(claim, before) == OrderWorkflowAction.ReconcilePending,
            "ORDER_COMPLETION_GUEST_PENDING");
        var expected = before.CompleteGuest(outcome);
        var action = outcome.Reservation.Step switch
        {
            "S01" => OrderWorkflowAction.AcceptCalculator,
            "S02" => OrderWorkflowAction.AcceptFormatter,
            "FINAL" => OrderWorkflowAction.AcceptOrder,
            _ => throw new InvalidOperationException("ORDER_COMPLETION_GUEST_STEP")
        };
        OrderCheck.Require(DevelopmentOrderWorkflow.Select(claim, expected) == action,
            "ORDER_COMPLETION_GUEST_ACTION");
        // The existing actor independently validates protected report,
        // scope and fresh absence before persisting the outcome.
        var acknowledged = await persist(outcome);
        OrderCheck.Require(acknowledged == expected
            && DevelopmentOrderWorkflow.Select(claim, acknowledged) == action, "ORDER_COMPLETION_GUEST_ACK");
        return acknowledged;
    }
}
