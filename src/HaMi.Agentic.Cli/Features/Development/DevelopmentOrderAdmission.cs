namespace HaMi.Agentic.Cli.Features.Development;

internal sealed record OrderAdmissionDecision(string Event, DevelopmentOrderOperationLedger Ledger);

// Bounded admission coordination for the reviewed pair. The callback must
// persist through the existing guarded journal and return its actual ACK.
// Neither an expected fold nor a recovered pending reservation is authority
// to dispatch; the existing owner, receipt and evidence gates still apply.
internal static class DevelopmentOrderAdmission
{
    internal static OrderAdmissionDecision Decide(DevelopmentOrderClaim claim,
        DevelopmentOrderOperationLedger before, OrderOperationReservation request,
        IReadOnlyList<OrderRequiredOperation> requiredAfter)
    {
        var action = DevelopmentOrderWorkflow.Select(claim, before);
        (string Step, string Kind)[] required = (action, request.Step, request.Kind) switch
        {
            (OrderWorkflowAction.GenerateCalculator, "S01", "model") =>
                [("S01", "guest"), ("S02", "model"), ("S02", "guest"), ("FINAL", "guest")],
            (OrderWorkflowAction.VerifyCalculator, "S01", "guest") =>
                [("S02", "model"), ("S02", "guest"), ("FINAL", "guest")],
            (OrderWorkflowAction.GenerateFormatter, "S02", "model") =>
                [("S02", "guest"), ("FINAL", "guest")],
            (OrderWorkflowAction.VerifyFormatter, "S02", "guest") =>
                [("FINAL", "guest")],
            (OrderWorkflowAction.VerifyIntegration, "FINAL", "guest") => [],
            _ => throw new InvalidOperationException("ORDER_ADMISSION_ACTION")
        };
        OrderCheck.Require(requiredAfter.Select(x => (x.Step, x.Kind)).SequenceEqual(required),
            "ORDER_ADMISSION_REQUIRED_WORK");
        return DecideBudget(claim.JobId, before, request, requiredAfter);
    }

    // Shared pure budget decision. Only the reviewed host determines
    // eligibility and mandatory work; the historical synthetic bridge
    // fixture also uses this budget-only part without pair semantics.
    internal static OrderAdmissionDecision DecideBudget(string jobId, DevelopmentOrderOperationLedger before,
        OrderOperationReservation request, IReadOnlyList<OrderRequiredOperation> requiredAfter)
    {
        OrderCheck.Require(before.Pending is null && before.BudgetStop is null && before.SoftStop is null
            && before.AcceptedOrder is null, "ORDER_ADMISSION_BLOCKED");
        var limits = before.CurrentLimits ?? throw new InvalidOperationException("ORDER_BUDGET_REVISION");
        string? hard = before.BudgetDenial(jobId, limits, request);
        string? soft = before.SoftStopDenial(jobId, limits, request, requiredAfter);
        if (hard is not null)
        {
            var stop = new OrderBudgetExhausted(1, request, hard);
            return new(DevelopmentOrderOperationLedger.EncodeBudgetStop(stop), before.StopForBudget(jobId, limits, stop));
        }

        if (soft is not null)
        {
            var stop = new OrderSoftStop(1, request, requiredAfter.ToArray(), soft);
            return new(DevelopmentOrderOperationLedger.EncodeSoftStop(stop), before.StopSoft(jobId, limits, stop));
        }

        return new(DevelopmentOrderOperationLedger.EncodeReservation(request),
            before.ReserveWithRequiredWork(jobId, limits, request, requiredAfter));
    }

    // Validate an actual replay segment between two adjacent work phases.
    // The host supplies genuine journal events and their authenticated
    // approval evidence; this fold does not authenticate a human decision.
    internal static void RequireContinuation(DevelopmentOrderClaim claim, DevelopmentOrderBudgetPolicy policy,
        DevelopmentOrderOperationLedger origin, DevelopmentOrderOperationLedger resumed,
        IReadOnlyList<string> events, IReadOnlyList<OrderRequiredOperation> requiredAfter)
    {
        OrderCheck.Require(events.Count >= 2 && origin.PolicyHash == policy.Sha256,
            "ORDER_CONTINUATION_HISTORY");
        var action = DevelopmentOrderWorkflow.Select(claim, origin);
        string first = events[0];
        var request = first.StartsWith("budget-stop-v1:", StringComparison.Ordinal)
            ? DevelopmentOrderOperationLedger.DecodeBudgetStop(first).Request
            : first.StartsWith("soft-stop-v1:", StringComparison.Ordinal)
                ? DevelopmentOrderOperationLedger.DecodeSoftStop(first).Request
                : throw new InvalidOperationException("ORDER_CONTINUATION_STOP");
        var stop = Decide(claim, origin, request, requiredAfter);
        OrderCheck.Require(stop.Event == first
            && DevelopmentOrderWorkflow.Select(claim, stop.Ledger) == OrderWorkflowAction.AwaitBudgetExtension,
            "ORDER_CONTINUATION_STOP");
        var folded = stop.Ledger;
        for (int i = 1; i < events.Count; i++)
        {
            var extension = DevelopmentOrderOperationLedger.DecodeBudgetExtension(events[i]);
            OrderCheck.Require(DevelopmentOrderOperationLedger.EncodeBudgetExtension(extension) == events[i],
                "ORDER_CONTINUATION_EVENT");
            folded = folded.Extend(claim.JobId, policy, extension);
        }

        OrderCheck.Require(folded == resumed && folded.Pending is null
            && folded.BudgetStop is null && folded.SoftStop is null
            && DevelopmentOrderWorkflow.Select(claim, resumed) == action,
            "ORDER_CONTINUATION_HISTORY");
    }

    // The host independently recomputes the decision under its journal
    // ownership and persists exactly one event before acknowledging it.
    internal static async Task<DevelopmentOrderOperationLedger> AdmitDecisionAsync(
        DevelopmentOrderClaim claim, DevelopmentOrderOperationLedger before,
        OrderOperationReservation request, IReadOnlyList<OrderRequiredOperation> requiredAfter,
        Func<OrderOperationReservation, Task<DevelopmentOrderOperationLedger>> persist)
    {
        var decision = Decide(claim, before, request, requiredAfter);
        var acknowledged = await persist(request);
        RequireAcknowledged(claim, decision, acknowledged);
        return acknowledged;
    }

    internal static void RequireAcknowledged(DevelopmentOrderClaim claim, OrderAdmissionDecision decision,
        DevelopmentOrderOperationLedger acknowledged)
    {
        // SoftStop contains an array; compare canonical event bytes rather
        // than reference equality of independently folded/restored arrays.
        var expected = decision.Ledger;
        OrderCheck.Require((expected with { SoftStop = null }) == (acknowledged with { SoftStop = null })
            && (expected.SoftStop is null ? acknowledged.SoftStop is null
                : acknowledged.SoftStop is not null
                    && DevelopmentOrderOperationLedger.EncodeSoftStop(expected.SoftStop)
                        == DevelopmentOrderOperationLedger.EncodeSoftStop(acknowledged.SoftStop)),
            "ORDER_ADMISSION_ACK");
        var expectedAction = expected.Pending is not null
            ? OrderWorkflowAction.ReconcilePending : OrderWorkflowAction.AwaitBudgetExtension;
        OrderCheck.Require(DevelopmentOrderWorkflow.Select(claim, acknowledged) == expectedAction,
            "ORDER_ADMISSION_ACK");
    }

    internal static async Task<DevelopmentOrderOperationLedger> AdmitAsync(
        DevelopmentOrderClaim claim, DevelopmentOrderOperationLedger before,
        OrderOperationReservation request, IReadOnlyList<OrderRequiredOperation> requiredAfter,
        Func<OrderOperationReservation, Task<DevelopmentOrderOperationLedger>> persist)
    {
        var decision = Decide(claim, before, request, requiredAfter);
        // Existing reservation-only callbacks cannot persist stops. Do not
        // hand a denied proposal to them. Stop-aware host integration follows.
        OrderCheck.Require(decision.Ledger.Pending == request, "ORDER_ADMISSION_BUDGET_STOP");
        var acknowledged = await persist(request);
        OrderCheck.Require(acknowledged == decision.Ledger
            && DevelopmentOrderWorkflow.Select(claim, acknowledged) == OrderWorkflowAction.ReconcilePending,
            "ORDER_ADMISSION_ACK");
        return acknowledged;
    }
}
