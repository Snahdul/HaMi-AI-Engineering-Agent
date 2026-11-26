using System.Globalization;
using System.Text;

namespace HaMi.Agentic.Cli.Features.Development;

// Pure journal reducer for the reviewed sequential two-step profile.
// Never dispatches work. Actor/host must separately enforce step eligibility,
// current authority, timeouts and independently verified outcome evidence.
internal sealed record OrderOperationLimits(
    int Models, int Guests, int Seconds, int S01Models, int S01Guests,
    int S02Models, int S02Guests, int FinalGuests);

internal sealed record OrderExecutionStart(int Version, string ArchiveHash, long UtcTicks);

// Compact immutable correlation with an already validated OrderAssignment.
// It grants no capability; the host must validate the assignment against its
// trusted catalog and current authorization before dispatch.
internal sealed record OrderOperationAgentBinding(int Revision, OrderAgentIdentity Agent, string AssignmentHash)
{
    internal static OrderOperationAgentBinding FromAssignment(OrderAssignment assignment) =>
        new(assignment.Revision, assignment.Agent,
            DevelopmentJobState.Hash(OrderStorageJson.Encode(assignment, DevelopmentJobState.MaxBytes)));

    internal void Validate()
    {
        OrderAgentIdentity agent = Agent ?? throw new InvalidOperationException("ORDER_OPERATION_AGENT");
        OrderCheck.Require(Revision > 0 && OrderCheck.Hash(AssignmentHash)
            && OrderCheck.Text(agent.Id, 100) && OrderCheck.Text(agent.Version, 100)
            && OrderCheck.Text(agent.Provider, 100) && OrderCheck.Text(agent.Model, 100)
            && OrderCheck.Hash(agent.ProfileHash) && OrderCheck.Hash(agent.ConfigurationHash),
            "ORDER_OPERATION_AGENT");
    }

    internal void RequireAssignment(OrderAssignment assignment)
    {
        Validate();
        OrderCheck.Require(this == FromAssignment(assignment), "ORDER_OPERATION_ASSIGNMENT");
    }
}

internal sealed record OrderOperationReservation(
    int Version, string OperationId, string Step, string Kind, int ReservedSeconds, long ObservedUtcTicks,
    OrderOperationAgentBinding? Assignment, string? SnapshotHash = null);

internal sealed record OrderOperationCompletion(
    int Version, string OperationId, string EvidenceHash, string Outcome, long ObservedUtcTicks);

internal sealed record OrderBudgetExhausted(int Version, OrderOperationReservation Request, string Reason);

internal sealed record OrderBudgetExtensionApproved(
    int Version, string JobId, string ArchiveHash, int PreviousRevision,
    OrderOperationLimits Limits, string ApprovalHash, long ObservedUtcTicks);

internal sealed record DevelopmentOrderOperationLedger(
    int Sequence = 0, int S01Models = 0, int S01Guests = 0,
    int S02Models = 0, int S02Guests = 0, int FinalGuests = 0,
    int ReservedSeconds = 0, OrderOperationReservation? Pending = null,
    long StartedUtcTicks = 0, long LastUtcTicks = 0, string? LastOutcome = null,
    string? LastEvidenceHash = null, OrderOperationLimits? CurrentLimits = null,
    string? ArchiveHash = null, string? PolicyHash = null, int FinalReserveSeconds = 0,
    int BudgetRevision = 0, OrderBudgetExhausted? BudgetStop = null, string ExtensionHashes = "",
    OrderCandidateCheckpoint? LatestCandidate = null, OrderGuestOutcome? LatestGuestOutcome = null,
    OrderOperationReservation? LatestModelReservation = null, OrderOperationCompletion? LatestModelCompletion = null)
{
    internal int Models => S01Models + S02Models;
    internal int Guests => S01Guests + S02Guests + FinalGuests;
    internal const int EventMaxBytes = 4096;

    // A reservation is identified by the immutable job and next journaled
    // sequence, not by a transient actor address or regenerated GUID.
    internal DevelopmentOrderOperationLedger Start(OrderExecutionStart start, string archiveHash,
        OrderOperationLimits limits, DevelopmentOrderBudgetPolicy policy)
    {
        policy.ValidateEffective(limits);
        OrderCheck.Require(StartedUtcTicks == 0 && Sequence == 0 && start.Version == 1
            && OrderCheck.Hash(archiveHash) && start.ArchiveHash == archiveHash
            && limits.Seconds > 0 && start.UtcTicks > 0
            && start.UtcTicks <= DateTime.MaxValue.Ticks - (long)limits.Seconds * TimeSpan.TicksPerSecond,
            "ORDER_EXECUTION_START");
        return this with
        {
            StartedUtcTicks = start.UtcTicks,
            LastUtcTicks = start.UtcTicks,
            CurrentLimits = limits,
            ArchiveHash = archiveHash,
            PolicyHash = policy.Sha256,
            FinalReserveSeconds = policy.FinalVerificationSeconds
        };
    }

    internal OrderOperationReservation Propose(string jobId, string step, string kind, int seconds, long now,
        OrderOperationAgentBinding? assignment, string? snapshotHash = null) =>
        new(4, OperationId(jobId, checked(Sequence + 1)), step, kind, seconds, now, assignment, snapshotHash);

    // The persisted UTC window includes downtime. Never reset it on reentry.
    // Trust the designated host clock; reject observed rollback, and use a
    // monotonic local cancellation deadline for actual dispatch.
    private void RequireObservation(long now) =>
        OrderCheck.Require(StartedUtcTicks > 0 && now >= LastUtcTicks
            && now <= DateTime.MaxValue.Ticks, "ORDER_EXECUTION_CLOCK");

    internal TimeSpan Remaining(OrderOperationLimits limits, long now)
    {
        RequireObservation(now);
        OrderCheck.Require(limits == CurrentLimits, "ORDER_BUDGET_REVISION");
        long end = checked(StartedUtcTicks + (long)limits.Seconds * TimeSpan.TicksPerSecond);
        return TimeSpan.FromTicks(Math.Max(0, end - now));
    }

    // Use only after the fresh Persist acknowledgement, never to redispatch
    // a recovered pending operation. Both windows include journal/ack time.
    internal TimeSpan DispatchAllowance(OrderOperationLimits limits, string operationId, long now)
    {
        OrderCheck.Require(Pending is not null && Pending.OperationId == operationId,
            "ORDER_OPERATION_DISPATCH_ID");
        var reservation = Pending ?? throw new InvalidOperationException("ORDER_OPERATION_DISPATCH_ID");
        TimeSpan remaining = Remaining(limits, now);
        long elapsed = now - reservation.ObservedUtcTicks;
        long operationTicks = (long)reservation.ReservedSeconds * TimeSpan.TicksPerSecond - elapsed;
        long closingTicks = reservation.Step == "FINAL" ? 0 : (long)FinalReserveSeconds * TimeSpan.TicksPerSecond;
        long allowed = Math.Min(remaining.Ticks - closingTicks, operationTicks);
        OrderCheck.Require(allowed > 0, "ORDER_OPERATION_DISPATCH_EXPIRED");
        return TimeSpan.FromTicks(allowed);
    }

    // A malformed proposal is not budget exhaustion. Only a well-formed,
    // eligible operation that cannot fit may produce the durable stop event.
    internal string? BudgetDenial(string jobId, OrderOperationLimits limits, OrderOperationReservation reservation)
    {
        OrderCheck.Require(Pending is null, "ORDER_OPERATION_PENDING");
        OrderCheck.Require(reservation.Version == 4
            && reservation.OperationId == OperationId(jobId, checked(Sequence + 1))
            && reservation.ReservedSeconds > 0, "ORDER_OPERATION_ID");
        OrderCheck.Require((reservation.Step, reservation.Kind) is
            ("S01" or "S02", "model" or "guest") or ("FINAL", "guest"), "ORDER_OPERATION_KIND");
        if (reservation.Kind == "model")
        {
            OrderCheck.Require(reservation.SnapshotHash is null, "ORDER_MODEL_SNAPSHOT");
            (reservation.Assignment ?? throw new InvalidOperationException("ORDER_OPERATION_ASSIGNMENT")).Validate();
        }
        else
            _ = DevelopmentOrderGuestReconciliation.RunId(reservation);
        OrderCheck.Require(FinalGuests == 0 || reservation.Step == "FINAL", "ORDER_FINAL_ALREADY_RESERVED");
        int closing = reservation.Step == "FINAL" ? 0 : FinalReserveSeconds;
        OrderCheck.Require(reservation.Step != "FINAL" || reservation.ReservedSeconds >= FinalReserveSeconds,
            "ORDER_FINAL_TIME_TOO_SHORT");
        TimeSpan remaining = Remaining(limits, reservation.ObservedUtcTicks);
        long requested = (long)reservation.ReservedSeconds + closing;
        if (requested > limits.Seconds - ReservedSeconds
            || requested * TimeSpan.TicksPerSecond > remaining.Ticks)
            return "time";
        bool fits = (reservation.Step, reservation.Kind) switch
        {
            ("S01", "model") => S01Models < limits.S01Models && Models < limits.Models,
            ("S02", "model") => S02Models < limits.S02Models && Models < limits.Models,
            ("S01", "guest") => S01Guests < limits.S01Guests && Guests < limits.Guests - (limits.FinalGuests - FinalGuests),
            ("S02", "guest") => S02Guests < limits.S02Guests && Guests < limits.Guests - (limits.FinalGuests - FinalGuests),
            ("FINAL", "guest") => FinalGuests < limits.FinalGuests && Guests < limits.Guests,
            _ => false
        };
        return fits ? null : "count";
    }

    internal DevelopmentOrderOperationLedger Reserve(string jobId, OrderOperationLimits limits,
        OrderOperationReservation reservation)
    {
        OrderCheck.Require(BudgetStop is null, "ORDER_BUDGET_EXHAUSTED");
        OrderCheck.Require(BudgetDenial(jobId, limits, reservation) is null, "ORDER_OPERATION_BUDGET");
        var charged = (reservation.Step, reservation.Kind) switch
        {
            ("S01", "model") => this with { S01Models = S01Models + 1 },
            ("S01", "guest") => this with { S01Guests = S01Guests + 1 },
            ("S02", "model") => this with { S02Models = S02Models + 1 },
            ("S02", "guest") => this with { S02Guests = S02Guests + 1 },
            ("FINAL", "guest") => this with { FinalGuests = FinalGuests + 1 },
            _ => throw new InvalidOperationException("ORDER_OPERATION_KIND")
        };
        return charged with
        {
            Sequence = checked(Sequence + 1),
            ReservedSeconds = checked(ReservedSeconds + reservation.ReservedSeconds),
            Pending = reservation,
            LastUtcTicks = reservation.ObservedUtcTicks
        };
    }

    internal DevelopmentOrderOperationLedger StopForBudget(string jobId, OrderOperationLimits limits,
        OrderBudgetExhausted stop)
    {
        OrderCheck.Require(BudgetStop is null && stop.Version == 1
            && stop.Reason is "time" or "count"
            && BudgetDenial(jobId, limits, stop.Request) == stop.Reason, "ORDER_BUDGET_STOP");
        return this with { BudgetStop = stop, LastUtcTicks = stop.Request.ObservedUtcTicks };
    }

    // Only folds an already authenticated human decision. The host must
    // independently check its exact evidence and current stricter ceilings.
    // The archived policy is the historical ceiling for deterministic replay.
    internal DevelopmentOrderOperationLedger Extend(string jobId, DevelopmentOrderBudgetPolicy policy,
        OrderBudgetExtensionApproved extension)
    {
        var stop = BudgetStop ?? throw new InvalidOperationException("ORDER_BUDGET_NOT_STOPPED");
        var old = CurrentLimits ?? throw new InvalidOperationException("ORDER_BUDGET_REVISION");
        OrderCheck.Require(Pending is null && extension.Version == 1 && extension.JobId == jobId
            && stop.Request.OperationId == OperationId(jobId, checked(Sequence + 1))
            && extension.ArchiveHash == ArchiveHash && policy.Sha256 == PolicyHash
            && extension.PreviousRevision == BudgetRevision && OrderCheck.Hash(extension.ApprovalHash)
            && !ExtensionHashes.Contains(extension.ApprovalHash, StringComparison.Ordinal), "ORDER_BUDGET_EXTENSION_BINDING");
        RequireObservation(extension.ObservedUtcTicks);
        var next = extension.Limits;
        policy.ValidateEffective(next);
        OrderCheck.Require(next != old && next.Models >= old.Models && next.Guests >= old.Guests
            && next.Seconds >= old.Seconds && next.S01Models >= old.S01Models && next.S01Guests >= old.S01Guests
            && next.S02Models >= old.S02Models && next.S02Guests >= old.S02Guests
            && next.FinalGuests >= old.FinalGuests
            && StartedUtcTicks <= DateTime.MaxValue.Ticks - (long)next.Seconds * TimeSpan.TicksPerSecond,
            "ORDER_BUDGET_EXTENSION_LIMITS");
        var extended = this with
        {
            CurrentLimits = next,
            BudgetRevision = checked(BudgetRevision + 1),
            LastUtcTicks = extension.ObservedUtcTicks,
            BudgetStop = null,
            ExtensionHashes = ExtensionHashes + extension.ApprovalHash + ";"
        };
        var request = stop.Request with { ObservedUtcTicks = extension.ObservedUtcTicks };
        string? denial = extended.BudgetDenial(jobId, next, request);
        return denial is null ? extended : extended with { BudgetStop = new(1, request, denial) };
    }

    // Descriptor only; the host verifies exact private bytes before append
    // and before any reuse after replay. A candidate never settles Pending.
    internal DevelopmentOrderOperationLedger Checkpoint(DevelopmentOrderClaim claim, OrderCandidateCheckpoint candidate)
    {
        int sequence = candidate.ValidateBinding(claim);
        OrderCheck.Require(ArchiveHash == claim.ArchiveHash && Pending is { Kind: "model" }
            && Pending.Step == candidate.Step && Pending.OperationId == candidate.OperationId
            && sequence == Sequence && LatestCandidate?.OperationId != candidate.OperationId,
            "ORDER_CANDIDATE_PENDING");
        RequireObservation(candidate.ObservedUtcTicks);
        return this with { LatestCandidate = candidate, LastUtcTicks = candidate.ObservedUtcTicks };
    }

    // This is only the fold of an already established outcome. A hash alone
    // is not evidence: caller must verify the actual bytes and provenance
    // before persisting this event. A known failure is also a spent operation.
    internal DevelopmentOrderOperationLedger Complete(OrderOperationCompletion completion)
    {
        var reservation = Pending ?? throw new InvalidOperationException("ORDER_MODEL_COMPLETION_REQUIRED");
        OrderCheck.Require(reservation.Kind == "model", "ORDER_MODEL_COMPLETION_REQUIRED");
        return Settle(completion) with
        {
            LatestModelReservation = reservation,
            LatestModelCompletion = completion
        };
    }

    // Only the typed guest outcome may settle a guest reservation. The
    // trusted host independently verifies report bytes and sandbox absence
    // before journaling; pure replay does not repeat inspect or guest work.
    internal DevelopmentOrderOperationLedger CompleteGuest(OrderGuestOutcome outcome)
    {
        var reservation = Pending ?? throw new InvalidOperationException("ORDER_GUEST_PENDING");
        string archiveHash = ArchiveHash ?? throw new InvalidOperationException("ORDER_GUEST_ARCHIVE");
        outcome.Validate(archiveHash, reservation);
        string evidenceHash = DevelopmentJobState.Hash(OrderStorageJson.Encode(outcome, EventMaxBytes));
        return Settle(new(2, reservation.OperationId, evidenceHash, "succeeded", outcome.ObservedUtcTicks))
            with
        { LatestGuestOutcome = outcome };
    }

    private DevelopmentOrderOperationLedger Settle(OrderOperationCompletion completion)
    {
        OrderCheck.Require(completion.Version == 2 && Pending is not null
            && completion.OperationId == Pending.OperationId && OrderCheck.Hash(completion.EvidenceHash)
            && completion.Outcome is "succeeded" or "failed", "ORDER_OPERATION_COMPLETION");
        RequireObservation(completion.ObservedUtcTicks);
        // Late evidence may settle a spent operation; it does not extend the
        // window or permit a new dispatch after expiry.
        return this with
        {
            Pending = null,
            LastUtcTicks = completion.ObservedUtcTicks,
            LastOutcome = completion.Outcome,
            LastEvidenceHash = completion.EvidenceHash
        };
    }

    private static string OperationId(string jobId, int sequence)
    {
        OrderCheck.Require(jobId is { Length: 32 }
            && jobId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && sequence > 0,
            "ORDER_OPERATION_JOB");
        return jobId + ":" + sequence.ToString(CultureInfo.InvariantCulture);
    }

    internal static string EncodeStart(OrderExecutionStart start) =>
        "start-v1:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(start, EventMaxBytes));

    internal static OrderExecutionStart DecodeStart(string evt) =>
        OrderStorageJson.Decode<OrderExecutionStart>(EventBytes(evt, "start-v1:"), EventMaxBytes);

    internal static string EncodeReservation(OrderOperationReservation reservation) =>
        "reserve-v4:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(reservation, EventMaxBytes));

    internal static OrderOperationReservation DecodeReservation(string evt) =>
        OrderStorageJson.Decode<OrderOperationReservation>(EventBytes(evt, "reserve-v4:"), EventMaxBytes);

    internal static string EncodeCompletion(OrderOperationCompletion completion) =>
        "complete-v2:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(completion, EventMaxBytes));

    internal static OrderOperationCompletion DecodeCompletion(string evt) =>
        OrderStorageJson.Decode<OrderOperationCompletion>(EventBytes(evt, "complete-v2:"), EventMaxBytes);

    internal static string EncodeBudgetStop(OrderBudgetExhausted stop) =>
        "budget-stop-v1:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(stop, EventMaxBytes));

    internal static OrderBudgetExhausted DecodeBudgetStop(string evt) =>
        OrderStorageJson.Decode<OrderBudgetExhausted>(EventBytes(evt, "budget-stop-v1:"), EventMaxBytes);

    internal static string EncodeBudgetExtension(OrderBudgetExtensionApproved extension) =>
        "budget-extension-v1:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(extension, EventMaxBytes));

    internal static OrderBudgetExtensionApproved DecodeBudgetExtension(string evt) =>
        OrderStorageJson.Decode<OrderBudgetExtensionApproved>(EventBytes(evt, "budget-extension-v1:"), EventMaxBytes);

    internal static string EncodeCandidate(OrderCandidateCheckpoint candidate) =>
        "candidate-v1:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(candidate, EventMaxBytes));

    internal static OrderCandidateCheckpoint DecodeCandidate(string evt) =>
        OrderStorageJson.Decode<OrderCandidateCheckpoint>(EventBytes(evt, "candidate-v1:"), EventMaxBytes);

    internal static string EncodeGuestOutcome(OrderGuestOutcome outcome) =>
        "guest-outcome-v1:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(outcome, EventMaxBytes));

    internal static OrderGuestOutcome DecodeGuestOutcome(string evt) =>
        OrderStorageJson.Decode<OrderGuestOutcome>(EventBytes(evt, "guest-outcome-v1:"), EventMaxBytes);

    private static byte[] EventBytes(string evt, string prefix)
    {
        OrderCheck.Require(evt.StartsWith(prefix, StringComparison.Ordinal)
            && evt.Length <= EventMaxBytes + prefix.Length, "ORDER_OPERATION_EVENT");
        return new UTF8Encoding(false, true).GetBytes(evt[prefix.Length..]);
    }
}
