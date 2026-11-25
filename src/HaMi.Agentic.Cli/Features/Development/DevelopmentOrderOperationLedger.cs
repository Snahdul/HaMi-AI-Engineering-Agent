using System.Globalization;
using System.Text;

namespace HaMi.Agentic.Cli.Features.Development;

// Pure journal reducer for the reviewed sequential two-step profile.
// Never dispatches work. Actor/host must separately enforce step eligibility,
// current authority, timeouts and independently verified outcome evidence.
internal sealed record OrderOperationLimits(
    int Models, int Guests, int Seconds, int S01Models, int S01Guests,
    int S02Models, int S02Guests, int FinalGuests)
{
    internal static OrderOperationLimits From(DevelopmentOrder order)
    {
        var root = order.Document;
        var limits = root.GetProperty("limits");
        var steps = root.GetProperty("steps");
        return new(limits.GetProperty("modelCalls").GetInt32(),
            limits.GetProperty("sandboxRuns").GetInt32(),
            limits.GetProperty("elapsedSeconds").GetInt32(),
            steps[0].GetProperty("maxModelCalls").GetInt32(),
            steps[0].GetProperty("maxSandboxRuns").GetInt32(),
            steps[1].GetProperty("maxModelCalls").GetInt32(),
            steps[1].GetProperty("maxSandboxRuns").GetInt32(),
            root.GetProperty("finalVerification").GetProperty("maxSandboxRuns").GetInt32());
    }
}

internal sealed record OrderOperationReservation(
    int Version, string OperationId, string Step, string Kind, int ReservedSeconds);

internal sealed record OrderOperationCompletion(
    int Version, string OperationId, string EvidenceHash);

internal sealed record DevelopmentOrderOperationLedger(
    int Sequence = 0, int S01Models = 0, int S01Guests = 0,
    int S02Models = 0, int S02Guests = 0, int FinalGuests = 0,
    int ReservedSeconds = 0, OrderOperationReservation? Pending = null)
{
    internal int Models => S01Models + S02Models;
    internal int Guests => S01Guests + S02Guests + FinalGuests;
    internal const int EventMaxBytes = 1024;

    // A reservation is identified by the immutable job and next journaled
    // sequence, not by a transient actor address or regenerated GUID.
    internal OrderOperationReservation Propose(string jobId, string step, string kind, int seconds) =>
        new(1, OperationId(jobId, checked(Sequence + 1)), step, kind, seconds);

    internal DevelopmentOrderOperationLedger Reserve(string jobId, OrderOperationLimits limits,
        OrderOperationReservation reservation)
    {
        OrderCheck.Require(Pending is null, "ORDER_OPERATION_PENDING");
        OrderCheck.Require(reservation.Version == 1
            && reservation.OperationId == OperationId(jobId, checked(Sequence + 1)),
            "ORDER_OPERATION_ID");
        OrderCheck.Require(reservation.ReservedSeconds > 0
            && reservation.ReservedSeconds <= limits.Seconds - ReservedSeconds, "ORDER_OPERATION_TIME");
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
            Pending = reservation
        };
    }

    // This is only the fold of an already established outcome. A hash alone
    // is not evidence: caller must verify the actual bytes and provenance
    // before persisting this event. A known failure is also a spent operation.
    internal DevelopmentOrderOperationLedger Complete(OrderOperationCompletion completion)
    {
        OrderCheck.Require(completion.Version == 1 && Pending is not null
            && completion.OperationId == Pending.OperationId && OrderCheck.Hash(completion.EvidenceHash),
            "ORDER_OPERATION_COMPLETION");
        return this with { Pending = null };
    }

    private static string OperationId(string jobId, int sequence)
    {
        OrderCheck.Require(jobId is { Length: 32 }
            && jobId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && sequence > 0,
            "ORDER_OPERATION_JOB");
        return jobId + ":" + sequence.ToString(CultureInfo.InvariantCulture);
    }

    internal static string EncodeReservation(OrderOperationReservation reservation) =>
        "reserve-v1:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(reservation, EventMaxBytes));

    internal static OrderOperationReservation DecodeReservation(string evt) =>
        OrderStorageJson.Decode<OrderOperationReservation>(EventBytes(evt, "reserve-v1:"), EventMaxBytes);

    internal static string EncodeCompletion(OrderOperationCompletion completion) =>
        "complete-v1:" + Encoding.UTF8.GetString(OrderStorageJson.Encode(completion, EventMaxBytes));

    internal static OrderOperationCompletion DecodeCompletion(string evt) =>
        OrderStorageJson.Decode<OrderOperationCompletion>(EventBytes(evt, "complete-v1:"), EventMaxBytes);

    private static byte[] EventBytes(string evt, string prefix)
    {
        OrderCheck.Require(evt.StartsWith(prefix, StringComparison.Ordinal)
            && evt.Length <= EventMaxBytes + prefix.Length, "ORDER_OPERATION_EVENT");
        return new UTF8Encoding(false, true).GetBytes(evt[prefix.Length..]);
    }
}
