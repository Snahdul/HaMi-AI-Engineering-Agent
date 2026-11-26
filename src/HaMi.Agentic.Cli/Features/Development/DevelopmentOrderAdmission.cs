namespace HaMi.Agentic.Cli.Features.Development;

// Bounded admission coordination for the reviewed pair. The callback must
// persist through the existing guarded journal and return its actual ACK.
// Neither an expected fold nor a recovered pending reservation is authority
// to dispatch; the existing owner, receipt and evidence gates still apply.
internal static class DevelopmentOrderAdmission
{
    internal static async Task<DevelopmentOrderOperationLedger> AdmitAsync(
        DevelopmentOrderClaim claim, DevelopmentOrderOperationLedger before,
        OrderOperationReservation request, IReadOnlyList<OrderRequiredOperation> requiredAfter,
        Func<OrderOperationReservation, Task<DevelopmentOrderOperationLedger>> persist)
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
        var limits = before.CurrentLimits ?? throw new InvalidOperationException("ORDER_BUDGET_REVISION");
        var expected = before.ReserveWithRequiredWork(claim.JobId, limits, request, requiredAfter);
        var acknowledged = await persist(request);
        OrderCheck.Require(acknowledged == expected
            && DevelopmentOrderWorkflow.Select(claim, acknowledged) == OrderWorkflowAction.ReconcilePending,
            "ORDER_ADMISSION_ACK");
        return acknowledged;
    }
}
