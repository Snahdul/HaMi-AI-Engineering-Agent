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

internal sealed record OrderOperationReservation(
    int Version, string OperationId, string Step, string Kind, int ReservedSeconds, long ObservedUtcTicks);

internal sealed record OrderOperationCompletion(
    int Version, string OperationId, string EvidenceHash, string Outcome, long ObservedUtcTicks);

internal sealed record DevelopmentOrderOperationLedger(
    int Sequence = 0, int S01Models = 0, int S01Guests = 0,
    int S02Models = 0, int S02Guests = 0, int FinalGuests = 0,
    int ReservedSeconds = 0, OrderOperationReservation? Pending = null,
    long StartedUtcTicks = 0, long LastUtcTicks = 0, string? LastOutcome = null,
    string? LastEvidenceHash = null)
{
    internal int Models => S01Models + S02Models;
    internal int Guests => S01Guests + S02Guests + FinalGuests;
    internal const int EventMaxBytes = 1024;

    // A reservation is identified by the immutable job and next journaled
    // sequence, not by a transient actor address or regenerated GUID.
    internal DevelopmentOrderOperationLedger Start(OrderExecutionStart start, string archiveHash,
        OrderOperationLimits limits)
    {
        OrderCheck.Require(StartedUtcTicks == 0 && Sequence == 0 && start.Version == 1
            && OrderCheck.Hash(archiveHash) && start.ArchiveHash == archiveHash
            && limits.Seconds > 0 && start.UtcTicks > 0
            && start.UtcTicks <= DateTime.MaxValue.Ticks - (long)limits.Seconds * TimeSpan.TicksPerSecond,
            "ORDER_EXECUTION_START");
        return this with { StartedUtcTicks = start.UtcTicks, LastUtcTicks = start.UtcTicks };
    }

    internal OrderOperationReservation Propose(string jobId, string step, string kind, int seconds, long now) =>
        new(2, OperationId(jobId, checked(Sequence + 1)), step, kind, seconds, now);

    // The persisted UTC window includes downtime. Never reset it on reentry.
    // Trust the designated host clock; reject observed rollback, and use a
    // monotonic local cancellation deadline for actual dispatch.
    private void RequireObservation(long now) =>
        OrderCheck.Require(StartedUtcTicks > 0 && now >= LastUtcTicks
            && now <= DateTime.MaxValue.Ticks, "ORDER_EXECUTION_CLOCK");

    internal TimeSpan Remaining(OrderOperationLimits limits, long now)
    {
        RequireObservation(now);
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
        long allowed = Math.Min(remaining.Ticks, operationTicks);
        OrderCheck.Require(allowed > 0, "ORDER_OPERATION_DISPATCH_EXPIRED");
        return TimeSpan.FromTicks(allowed);
    }

    internal DevelopmentOrderOperationLedger Reserve(string jobId, OrderOperationLimits limits,
        OrderOperationReservation reservation)
    {
        OrderCheck.Require(Pending is null, "ORDER_OPERATION_PENDING");
        OrderCheck.Require(reservation.Version == 2
            && reservation.OperationId == OperationId(jobId, checked(Sequence + 1)),
            "ORDER_OPERATION_ID");
        OrderCheck.Require(reservation.ReservedSeconds > 0
            && reservation.ReservedSeconds <= limits.Seconds - ReservedSeconds
            && TimeSpan.FromSeconds(reservation.ReservedSeconds) <= Remaining(limits, reservation.ObservedUtcTicks),
            "ORDER_OPERATION_TIME");
        var charged = (reservation.Step, reservation.Kind) switch
        {
            ("S01", "model") when S01Models < limits.S01Models && Models < limits.Models =>
                this with { S01Models = S01Models + 1 },
            ("S01", "guest") when S01Guests < limits.S01Guests && Guests < limits.Guests =>
                this with { S01Guests = S01Guests + 1 },
            ("S02", "model") when S02Models < limits.S02Models && Models < limits.Models =>
                this with { S02Models = S02Models + 1 },
            ("S02", "guest") when S02Guests < limits.S02Guests && Guests < limits.Guests =>
                this with { S02Guests = S02Guests + 1 },
            ("FINAL", "guest") when FinalGuests < limits.FinalGuests && Guests < limits.Guests =>
                this with { FinalGuests = FinalGuests + 1 },
            _ => throw new InvalidOperationException("ORDER_OPERATION_BUDGET_OR_KIND")
        };
        // Retain the final verification allowance even if the supplied
        // limits are inconsistent. No regular operation may consume it.
        OrderCheck.Require(reservation.Step == "FINAL"
            || charged.Guests <= limits.Guests - (limits.FinalGuests - FinalGuests),
            "ORDER_OPERATION_FINAL_ALLOWANCE");
        return charged with
        {
            Sequence = checked(Sequence + 1),
            ReservedSeconds = checked(ReservedSeconds + reservation.ReservedSeconds),
            Pending = reservation,
            LastUtcTicks = reservation.ObservedUtcTicks
        };
    }

    // This is only the fold of an already established outcome. A hash alone
    // is not evidence: caller must verify the actual bytes and provenance
    // before persisting this event. A known failure is also a spent operation.
    internal DevelopmentOrderOperationLedger Complete(OrderOperationCompletion completion)
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
        "reserve-v2:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(reservation, EventMaxBytes));

    internal static OrderOperationReservation DecodeReservation(string evt) =>
        OrderStorageJson.Decode<OrderOperationReservation>(EventBytes(evt, "reserve-v2:"), EventMaxBytes);

    internal static string EncodeCompletion(OrderOperationCompletion completion) =>
        "complete-v2:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(completion, EventMaxBytes));

    internal static OrderOperationCompletion DecodeCompletion(string evt) =>
        OrderStorageJson.Decode<OrderOperationCompletion>(EventBytes(evt, "complete-v2:"), EventMaxBytes);

    private static byte[] EventBytes(string evt, string prefix)
    {
        OrderCheck.Require(evt.StartsWith(prefix, StringComparison.Ordinal)
            && evt.Length <= EventMaxBytes + prefix.Length, "ORDER_OPERATION_EVENT");
        return new UTF8Encoding(false, true).GetBytes(evt[prefix.Length..]);
    }
}
