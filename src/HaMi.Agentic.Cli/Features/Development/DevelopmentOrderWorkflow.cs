using System.Globalization;

namespace HaMi.Agentic.Cli.Features.Development;

internal enum OrderWorkflowAction
{
    ReconcilePending,
    AwaitBudgetExtension,
    GenerateCalculator,
    VerifyCalculator,
    AcceptCalculator,
    GenerateFormatter,
    VerifyFormatter,
    AcceptFormatter,
    VerifyIntegration,
    AcceptOrder,
    InspectCompletedOrder,
    ReviewRejectedModel
}

// The replayed journal is the only workflow authority. This bounded pair
// projection chooses the next obligation, never authorizes or performs an
// effect. Hosts still require genuine replay views, current authority,
// protected evidence, independent observations and a fresh reservation ACK.
internal static class DevelopmentOrderWorkflow
{
    internal static OrderWorkflowAction Select(DevelopmentOrderClaim claim, DevelopmentOrderOperationLedger ledger)
    {
        OrderCheck.Require(ledger.ArchiveHash == claim.ArchiveHash && ledger.PolicyHash == claim.Binding.PolicyHash
            && ledger.CurrentLimits is not null && ledger.StartedUtcTicks > 0
            && ledger.Sequence is >= 0 and <= OrderCandidateCheckpoint.MaxSequence
            && ledger.S01Models >= 0 && ledger.S01Guests >= 0 && ledger.S02Models >= 0
            && ledger.S02Guests >= 0 && ledger.FinalGuests is >= 0 and <= 1
            && ledger.Sequence == ledger.Models + ledger.Guests, "ORDER_WORKFLOW_BINDING");
        string id = claim.JobId + ":" + ledger.Sequence.ToString(CultureInfo.InvariantCulture);
        var calculator = ledger.AcceptedS01;
        var pair = ledger.AcceptedS02;
        var terminal = ledger.AcceptedOrder;
        OrderCheck.Require((calculator is not null || ledger.S02Models == 0 && ledger.S02Guests == 0)
            && (pair is not null || ledger.FinalGuests == 0)
            && (pair is null || calculator is not null && pair.Predecessor == calculator)
            && (terminal is null || pair is not null && ledger.Pending is null
                && ledger.BudgetStop is null && ledger.SoftStop is null), "ORDER_WORKFLOW_ACCEPTANCE_CHAIN");
        // Unknown effects take precedence over a budget decision. Even a
        // missing dispatch receipt cannot make a recovered reservation fresh.
        if (ledger.Pending is { } pending)
        {
            OrderCheck.Require(pending.OperationId == id, "ORDER_WORKFLOW_PENDING");
            return OrderWorkflowAction.ReconcilePending;
        }
        if (ledger.BudgetStop is not null || ledger.SoftStop is not null)
            return OrderWorkflowAction.AwaitBudgetExtension;

        if (ledger.Sequence == 0)
        {
            OrderCheck.Require(calculator is null && pair is null && terminal is null
                && ledger.LatestCandidate is null && ledger.LatestGuestOutcome is null
                && ledger.LatestModelReservation is null && ledger.LatestModelCompletion is null
                && ledger.LastOutcome is null, "ORDER_WORKFLOW_INITIAL");
            return OrderWorkflowAction.GenerateCalculator;
        }

        if (ledger.LatestGuestOutcome is { } guest && guest.Reservation.OperationId == id)
        {
            guest.Validate(claim.ArchiveHash, guest.Reservation);
            OrderCheck.Require(ledger.LastOutcome == "succeeded"
                && ledger.LastEvidenceHash == DevelopmentJobState.Hash(
                    OrderStorageJson.Encode(guest, DevelopmentOrderOperationLedger.EventMaxBytes)),
                "ORDER_WORKFLOW_GUEST_HISTORY");
            return guest.Reservation.Step switch
            {
                "S01" when calculator is null => OrderWorkflowAction.AcceptCalculator,
                "S01" when calculator is not null && pair is null
                    && calculator.GuestOperationId == id && calculator.GuestReportHash == guest.ReportHash =>
                    OrderWorkflowAction.GenerateFormatter,
                "S02" when calculator is not null && pair is null => OrderWorkflowAction.AcceptFormatter,
                "S02" when pair is not null && pair.GuestOperationId == id && pair.GuestReportHash == guest.ReportHash =>
                    OrderWorkflowAction.VerifyIntegration,
                "FINAL" when pair is not null && terminal is null => OrderWorkflowAction.AcceptOrder,
                "FINAL" when pair is not null && terminal is not null
                    && terminal.ArchiveHash == claim.ArchiveHash
                    && terminal.PairAcceptanceHash == DevelopmentOrderOperationLedger.PairAcceptanceHash(pair)
                    && terminal.GuestOperationId == id && terminal.GuestReportHash == guest.ReportHash
                    && terminal.SnapshotHash == guest.Reservation.SnapshotHash => OrderWorkflowAction.InspectCompletedOrder,
                _ => throw new InvalidOperationException("ORDER_WORKFLOW_GUEST_STEP")
            };
        }

        var model = ledger.LatestModelReservation ?? throw new InvalidOperationException("ORDER_WORKFLOW_MODEL_HISTORY");
        var completion = ledger.LatestModelCompletion ?? throw new InvalidOperationException("ORDER_WORKFLOW_MODEL_HISTORY");
        OrderCheck.Require(model.OperationId == id && model.Kind == "model" && completion.OperationId == id
            && completion.Outcome == ledger.LastOutcome && completion.EvidenceHash == ledger.LastEvidenceHash
            && (model.Step == "S01" && calculator is null || model.Step == "S02" && calculator is not null && pair is null),
            "ORDER_WORKFLOW_MODEL_HISTORY");
        var candidate = ledger.LatestCandidate;
        if (completion.Outcome == "failed")
        {
            OrderCheck.Require(candidate?.OperationId != id, "ORDER_WORKFLOW_REJECTED_CANDIDATE");
            return OrderWorkflowAction.ReviewRejectedModel;
        }
        OrderCheck.Require(completion.Outcome == "succeeded" && candidate is not null
            && candidate.OperationId == id && candidate.Step == model.Step && candidate.Quality == "Generated",
            "ORDER_WORKFLOW_CANDIDATE");
        _ = (candidate ?? throw new InvalidOperationException("ORDER_WORKFLOW_CANDIDATE")).ValidateBinding(claim);
        if (model.Step == "S02")
            candidate.RequireFormatterInput(claim, calculator ?? throw new InvalidOperationException("ORDER_FORMATTER_PREDECESSOR"));
        return model.Step == "S01" ? OrderWorkflowAction.VerifyCalculator : OrderWorkflowAction.VerifyFormatter;
    }
}
